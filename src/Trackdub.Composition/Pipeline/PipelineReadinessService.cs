using System.Collections.Concurrent;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Pipeline;

/// <summary>
/// Evaluates pipeline readiness for all enabled stages against a set of runtime
/// model selections. Pure read — never opens dialogs or downloads models.
/// Lives in Composition because it references IRuntimePlanner (Inference layer).
/// </summary>
public sealed class PipelineReadinessService(
    IRuntimePlanner runtimePlanner,
    ICloudApiKeyProvider cloudApiKeyProvider,
    IConsentService consentService,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : IPipelineReadinessService
{
    private readonly IRuntimePlanner _runtimePlanner =
        runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly ICloudApiKeyProvider _cloudApiKeyProvider =
        cloudApiKeyProvider ?? throw new ArgumentNullException(nameof(cloudApiKeyProvider));
    private readonly IConsentService _consentService =
        consentService ?? throw new ArgumentNullException(nameof(consentService));
    private readonly IRuntimePlanningPreferences? _runtimePlanningPreferences = runtimePlanningPreferences;

    // Cache key: (stage, modelAlias, sourceLanguage, targetLanguage) → StageReadiness
    // Simple in-memory cache; invalidated on selection change via InvalidateCache().
    private readonly ConcurrentDictionary<(RuntimeStage Stage, string? ModelAlias, string? SourceLanguage, string? TargetLanguage), StageReadiness> _cache = new();

    public async Task<PipelineReadinessReport> EvaluateAsync(
        IReadOnlyList<RuntimeStage> enabledStages,
        RuntimeModelSelections selections,
        TranscriptProjectState? state,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null)
    {
        ArgumentNullException.ThrowIfNull(enabledStages);
        ArgumentNullException.ThrowIfNull(selections);

        var stageReadinesses = new List<StageReadiness>(enabledStages.Count);

        foreach (RuntimeStage stage in enabledStages)
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? modelAlias = GetModelAlias(stage, selections);
            string? planningSourceLanguageCode = stage == RuntimeStage.Translation
                ? TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(sourceLanguageCode)
                : sourceLanguageCode;
            string? planningTargetLanguageCode = stage == RuntimeStage.Translation
                ? TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCodeOrNull(targetLanguageCode)
                : targetLanguageCode;
            StageReadiness readiness;

            // Optional separation: if alias explicitly set to "skip", report as SkippableOptional.
            if (stage == RuntimeStage.Separation && IsSeparationSkipped(selections))
            {
                readiness = new StageReadiness(
                    StageName: StageNameFor(stage),
                    Status: ReadinessState.SkippableOptional,
                    Detail: "Separation is optional and currently disabled",
                    ModelId: null,
                    ModelAlias: null,
                    ResolveAction: null);
                stageReadinesses.Add(readiness);
                continue;
            }

            if (stage == RuntimeStage.TextRefinement && !selections.EnableAsrTextRefinement)
            {
                readiness = new StageReadiness(
                    StageName: StageNames.TextRefinementAsr,
                    Status: ReadinessState.SkippableOptional,
                    Detail: "ASR text polish is disabled.",
                    ModelId: null,
                    ModelAlias: null,
                    ResolveAction: null);
                stageReadinesses.Add(readiness);
                continue;
            }

            // Cache hit (keyed on stage + alias + language context).
            var cacheKey = (stage, modelAlias, planningSourceLanguageCode, planningTargetLanguageCode);
            if (_cache.TryGetValue(cacheKey, out readiness!))
            {
                stageReadinesses.Add(readiness);
                continue;
            }

            readiness = IsCloudAlias(stage, modelAlias)
                ? await EvaluateCloudStageAsync(stage, modelAlias!, cancellationToken).ConfigureAwait(false)
                : await EvaluateLocalStageAsync(
                    stage,
                    modelAlias,
                    selections,
                    planningSourceLanguageCode,
                    planningTargetLanguageCode,
                    cancellationToken).ConfigureAwait(false);

            // TTS: additionally check voice-clone consent when local TTS is ready.
            if (stage == RuntimeStage.Tts
                && readiness.Status == ReadinessState.Ready
                && !_consentService.IsVoiceCloningConsentGranted
                && HasVoiceCloneRequest(state))
            {
                readiness = readiness with
                {
                    Status = ReadinessState.ConsentRequired,
                    Detail = "Voice cloning requires session consent",
                    ResolveAction = "grant-consent",
                };
            }

            _cache[cacheKey] = readiness;
            stageReadinesses.Add(readiness);
        }

        return new PipelineReadinessReport(stageReadinesses);
    }

    public void InvalidateCache(IReadOnlyList<RuntimeStage>? stages = null)
    {
        if (stages is null)
        {
            _cache.Clear();
            return;
        }

        foreach (RuntimeStage stage in stages)
        {
            foreach (var key in _cache.Keys.Where(k => k.Stage == stage).ToList())
            {
                _cache.TryRemove(key, out _);
            }
        }
    }

    // ── Cloud stage evaluation ─────────────────────────────────────────────────

    private async Task<StageReadiness> EvaluateCloudStageAsync(
        RuntimeStage stage,
        string modelAlias,
        CancellationToken cancellationToken)
    {
        string providerKey = CloudProviderKey(modelAlias);
        string? apiKey = await _cloudApiKeyProvider
            .GetApiKeyAsync(providerKey, cancellationToken)
            .ConfigureAwait(false);

        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return new StageReadiness(
                StageName: StageNameFor(stage),
                Status: ReadinessState.CloudKeyMissing,
                Detail: $"{providerKey} API key is not configured",
                ModelId: null,
                ModelAlias: modelAlias,
                ResolveAction: "set-api-key");
        }

        return new StageReadiness(
            StageName: StageNameFor(stage),
            Status: ReadinessState.Ready,
            Detail: null,
            ModelId: null,
            ModelAlias: modelAlias,
            ResolveAction: null);
    }

    // ── Local stage evaluation ─────────────────────────────────────────────────

    private async Task<StageReadiness> EvaluateLocalStageAsync(
        RuntimeStage stage,
        string? modelAlias,
        RuntimeModelSelections selections,
        string? sourceLanguageCode,
        string? targetLanguageCode,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest request = BuildPlanningRequest(
            stage,
            modelAlias,
            selections,
            sourceLanguageCode,
            targetLanguageCode);
        request = await StageRuntimePlanningRequestFactory
            .ApplyPreferredModelTierAsync(request, _runtimePlanningPreferences, cancellationToken)
            .ConfigureAwait(false);

        StageRuntimePlan plan = await _runtimePlanner
            .PlanAsync(request, cancellationToken)
            .ConfigureAwait(false);

        return MapPlanToReadiness(stage, plan);
    }

    private static StageReadiness MapPlanToReadiness(RuntimeStage stage, StageRuntimePlan plan)
    {
        string stageName = StageNameFor(stage);

        if (plan.Status is StageRuntimePlanStatus.Ready or StageRuntimePlanStatus.Verified)
        {
            return new StageReadiness(stageName, ReadinessState.Ready, null, plan.ModelId, plan.ModelAlias, null);
        }

        if (plan.Status == StageRuntimePlanStatus.DownloadRequired)
        {
            return new StageReadiness(
                stageName,
                ReadinessState.DownloadRequired,
                Detail: $"Model '{plan.ModelId}' needs to be downloaded",
                ModelId: plan.ModelId,
                ModelAlias: plan.ModelAlias,
                ResolveAction: "download");
        }

        // Blocked — inspect fallback code
        return plan.Fallback?.Code switch
        {
            RuntimePlanFallbackCode.CommercialSafeExcluded =>
                new StageReadiness(stageName, ReadinessState.CommercialBlocked,
                    "Model is non-commercial only; switch to a commercial-safe model",
                    plan.ModelId, plan.ModelAlias, null),

            RuntimePlanFallbackCode.ModelIntegrityMismatch =>
                new StageReadiness(stageName, ReadinessState.IntegrityFailed,
                    "Model checksum verification failed; re-download the model",
                    plan.ModelId, plan.ModelAlias, "download"),

            RuntimePlanFallbackCode.ProviderUnavailable =>
                new StageReadiness(stageName, ReadinessState.ProviderMissing,
                    plan.Fallback?.Detail ?? "No compatible execution provider found",
                    plan.ModelId, plan.ModelAlias, null),

            RuntimePlanFallbackCode.ProviderSmokeTestFailed =>
                new StageReadiness(stageName, ReadinessState.RuntimeMissing,
                    plan.Fallback?.Detail ?? "Execution provider smoke test failed; verify runtime installation",
                    plan.ModelId, plan.ModelAlias, "install-runtime"),

            _ =>
                new StageReadiness(stageName, ReadinessState.ImportRequired,
                    Detail: plan.Fallback is { } f
                        ? $"{f.Code}{(f.Detail is not null ? $": {f.Detail}" : string.Empty)}"
                        : "No compatible model found; import or download a model file",
                    plan.ModelId, plan.ModelAlias, "import"),
        };
    }

    // ── Helpers ────────────────────────────────────────────────────────────────

    private static string? GetModelAlias(RuntimeStage stage, RuntimeModelSelections selections) =>
        stage switch
        {
            RuntimeStage.Asr => selections.AsrModelAlias,
            RuntimeStage.Translation => selections.TranslationModelAlias,
            RuntimeStage.Tts => selections.TtsModelAlias,
            RuntimeStage.Separation => selections.SeparationModelAlias,
            RuntimeStage.OverlapRescue => selections.OverlapRescueModelAlias,
            RuntimeStage.Diarization => selections.DiarizationModelAlias,
            RuntimeStage.TextRefinement => selections.TextRefinementModelAlias,
            RuntimeStage.LipSync => selections.LipSyncModelAlias,
            RuntimeStage.LipSynthesis => selections.LipSynthesisModelAlias,
            _ => null,
        };

    private static bool IsCloudAlias(RuntimeStage stage, string? alias) =>
        stage switch
        {
            RuntimeStage.Asr => AsrModelOverrideSettings.IsCloudAlias(alias),
            RuntimeStage.Translation => IsCloudTranslationAlias(alias),
            RuntimeStage.Tts => TtsModelOverrideSettings.IsCloudAlias(alias),
            _ => false,
        };

    private static bool IsCloudTranslationAlias(string? alias) =>
        TranslationModelOverrideSettings.IsDeepLModelAlias(alias)
        || TranslationModelOverrideSettings.IsOpenAiGptAlias(alias)
        || TranslationModelOverrideSettings.IsGeminiTranslationAlias(alias);

    private static string CloudProviderKey(string? alias)
    {
        if (AsrModelOverrideSettings.IsOpenAiWhisperAlias(alias)) return "openai";
        if (AsrModelOverrideSettings.IsGeminiAsrAlias(alias)) return "gemini";
        if (TranslationModelOverrideSettings.IsDeepLModelAlias(alias)) return "deepl";
        if (TranslationModelOverrideSettings.IsOpenAiGptAlias(alias)) return "openai";
        if (TranslationModelOverrideSettings.IsGeminiTranslationAlias(alias)) return "gemini";
        if (TtsModelOverrideSettings.IsElevenLabsAlias(alias)) return "elevenlabs";
        if (TtsModelOverrideSettings.IsOpenAiTtsAlias(alias)) return "openai";
        if (TtsModelOverrideSettings.IsGoogleTtsAlias(alias)) return "google";
        return "unknown";
    }

    private static bool IsSeparationSkipped(RuntimeModelSelections selections) =>
        // Treat null/empty separation alias with Auto override as user-opted-out.
        // The explicit skip is signaled by SeparationModelOverride.Auto with no alias.
        selections.SeparationModelOverride == SeparationModelOverride.Auto
        && string.IsNullOrWhiteSpace(selections.SeparationModelAlias);

    private static StageRuntimePlanningRequest BuildPlanningRequest(
        RuntimeStage stage,
        string? modelAlias,
        RuntimeModelSelections selections,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null)
    {
        RuntimeModelRequestOptions options = RuntimeModelRequestFactory.CreateOptions(selections);

        return new(
            Stage: stage,
            PreferredModelAlias: modelAlias,
            PreferredExecutionProvider: selections.HardwareOverrides.TryGetValue(
                stage.ToString(), out ExecutionProviderKind ep) ? ep : null,
            PreferredModelVariantAlias: RuntimeModelRequestFactory.ResolvePreferredModelVariantAlias(
                options,
                stage,
                modelAlias),
            SourceLanguage: stage switch
            {
                RuntimeStage.Asr => sourceLanguageCode,
                RuntimeStage.Translation => sourceLanguageCode,
                _ => null,
            },
            TargetLanguage: stage == RuntimeStage.Translation ? targetLanguageCode : null);
    }

    private static bool HasVoiceCloneRequest(TranscriptProjectState? state) =>
        // Voice-clone consent only required when at least one voice assignment has a reference clip.
        state?.VoiceAssignments.Any(v => v.ReferenceClipArtifactId is not null) == true;

    private static string StageNameFor(RuntimeStage stage) =>
        stage switch
        {
            RuntimeStage.Vad => StageNames.Vad,
            RuntimeStage.Asr => StageNames.Asr,
            RuntimeStage.Diarization => StageNames.Diarization,
            RuntimeStage.Translation => StageNames.Translation,
            RuntimeStage.Tts => StageNames.Tts,
            RuntimeStage.Separation => StageNames.Separation,
            RuntimeStage.OverlapRescue => StageNames.OverlapRescue,
            RuntimeStage.TextRefinement => StageNames.TextRefinementAsr,
            RuntimeStage.LipSync => StageNames.LipSync,
            RuntimeStage.LipSynthesis => StageNames.LipSynthesis,
            _ => stage.ToString().ToLowerInvariant(),
        };
}
