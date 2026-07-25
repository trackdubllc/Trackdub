using Trackdub.Application.LipSynthesis;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed record RuntimeExecutionProviderSelection(
    ExecutionProviderKind? PreferredExecutionProvider,
    bool RequirePreferredExecutionProvider);

public sealed class RuntimeModelSetupCoordinator
{
    private static readonly SemaphoreSlim TextRefinementSetupGate = new(1, 1);

    public Task<RuntimeModelSetupResult> EnsureImportModelsAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        bool enableStemSeparation,
        RuntimeModelSetupCallbacks callbacks,
        bool allowOptionalStageSkip = false,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null) =>
        EnsureAvailableAsync(
            workspace,
            RuntimeModelRequestFactory.CreateImportRequests(
                RuntimeModelRequestFactory.CreateOptions(selections),
                enableStemSeparation,
                sourceLanguageCode),
            callbacks,
            allowOptionalStageSkip,
            cancellationToken);

    public Task<RuntimeModelSetupResult> EnsureStemRerunModelsAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        IReadOnlyList<RuntimeStage> stages,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null) =>
        EnsureAvailableAsync(
            workspace,
            RuntimeModelRequestFactory.CreateStemRerunRequests(
                RuntimeModelRequestFactory.CreateOptions(selections),
                stages,
                sourceLanguageCode),
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);

    public Task<RuntimeModelSetupResult> EnsureAsrModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default,
        string? sourceLanguageCode = null) =>
        EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateAsrRequest(RuntimeModelRequestFactory.CreateOptions(selections), sourceLanguageCode)],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);

    public Task<RuntimeModelSetupResult> EnsureDiarizationModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default) =>
        EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateDiarizationRequest(RuntimeModelRequestFactory.CreateOptions(selections))],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);

    public Task<RuntimeModelSetupResult> EnsureOverlapRescueModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default) =>
        EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateOverlapRescueRequest(RuntimeModelRequestFactory.CreateOptions(selections))],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);

    public Task<RuntimeModelSetupResult> EnsureTtsModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        bool requiresVoiceClone,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default) =>
        EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateTtsRequest(RuntimeModelRequestFactory.CreateOptions(selections), requiresVoiceClone)],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);

    public Task<RuntimeModelSetupResult> EnsureTranslationModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        string sourceLanguageCode,
        string targetLanguageCode,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default) =>
        EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateTranslationRequest(
                RuntimeModelRequestFactory.CreateOptions(selections),
                sourceLanguageCode,
                targetLanguageCode)],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);

    public async Task<RuntimeModelSetupResult> EnsureTextRefinementModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        RuntimeModelSetupCallbacks callbacks,
        CancellationToken cancellationToken = default)
    {
        await TextRefinementSetupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await EnsureAvailableAsync(
                workspace,
                [RuntimeModelRequestFactory.CreateTextRefinementRequest(RuntimeModelRequestFactory.CreateOptions(selections))],
                callbacks,
                allowOptionalStageSkip: false,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            TextRefinementSetupGate.Release();
        }
    }

    public Task<RuntimeModelSetupResult> EnsureLipSyncModelAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        RuntimeModelSetupCallbacks callbacks,
        string? preferredModelAlias = null,
        CancellationToken cancellationToken = default)
    {
        RuntimeModelRequestOptions options = RuntimeModelRequestFactory.CreateOptions(selections);
        return EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateLipSyncRequest(options, preferredModelAlias)],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken);
    }

    public async Task<RuntimeModelSetupResult> EnsureLipSynthesisModelsAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        RuntimeModelSetupCallbacks callbacks,
        string? preferredModelAlias = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RuntimeModelRequestOptions options = RuntimeModelRequestFactory.CreateOptions(selections);
        RuntimeModelSetupResult primaryResult = await EnsureAvailableAsync(
            workspace,
            [RuntimeModelRequestFactory.CreateLipSynthesisRequest(options, preferredModelAlias)],
            callbacks,
            allowOptionalStageSkip: false,
            cancellationToken).ConfigureAwait(false);

        if (!primaryResult.IsReady)
        {
            return primaryResult;
        }

        return await RuntimeModelSetupWorkflow.EnsureManifestCompanionModelsAvailableAsync(
            workspace.RuntimeModels,
            LipSynthesisModelRequirements.CompanionManifestAliases,
            RuntimeStage.LipSynthesis,
            callbacks,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<RequiredRuntimeModelStatus?> GetMissingTranslationModelStatusAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        string sourceLanguageCode,
        string targetLanguageCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        RuntimeModelRequest request = RuntimeModelRequestFactory.CreateTranslationRequest(
            RuntimeModelRequestFactory.CreateOptions(selections),
            sourceLanguageCode,
            targetLanguageCode);
        RequiredRuntimeModelStatus? status = await workspace.RuntimeModels
            .GetRequiredModelStatusAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return status is null || status.IsAvailable ? null : status;
    }

    public InferenceModelPreferences CreateModelPreferences(RuntimeModelSelections selections) =>
        RuntimeModelRequestFactory.CreateModelPreferences(selections);

    public static RuntimeExecutionProviderSelection CreateExecutionProviderSelection(
        RuntimeModelSelections selections,
        RuntimeStage stage)
    {
        InferenceModelPreferences preferences = RuntimeModelRequestFactory.CreateModelPreferences(selections);
        return new RuntimeExecutionProviderSelection(
            preferences.GetPreferredExecutionProvider(stage),
            preferences.RequiresPreferredExecutionProvider(stage));
    }

    public static string? ResolvePreferredModelVariantAlias(
        RuntimeModelSelections selections,
        RuntimeStage stage,
        string? preferredModelAlias = null) =>
        RuntimeModelRequestFactory.ResolvePreferredModelVariantAlias(
            RuntimeModelRequestFactory.CreateOptions(selections),
            stage,
            preferredModelAlias);

    public static RerunDiarizationRequest CreateRerunDiarizationRequest(RuntimeModelSelections selections) =>
        RuntimeModelRequestFactory.CreateRerunDiarizationRequest(RuntimeModelRequestFactory.CreateOptions(selections));

    public static RetranscribeTranscriptSegmentsRequest CreateRetranscribeRequest(
        RuntimeModelSelections selections,
        Guid transcriptRevisionId,
        IReadOnlyList<Guid> segmentIds) =>
        RuntimeModelRequestFactory.CreateRetranscribeRequest(
            RuntimeModelRequestFactory.CreateOptions(selections),
            transcriptRevisionId,
            segmentIds);

    /// <summary>
    /// Consolidated pre-run provisioning gate. Evaluates the readiness report, builds a
    /// single batched request list for all stages that need a download or import, and runs
    /// the setup workflow once — replacing N scattered per-stage Ensure* dialog calls.
    /// Cloud-key and consent blocking states are not handled here; they are resolved via
    /// their own dialogs before this is called.
    /// </summary>
    public async Task<RuntimeModelSetupResult> EnsurePipelineModelsAvailableAsync(
        TranscriptWorkspace workspace,
        RuntimeModelSelections selections,
        PipelineReadinessReport report,
        RuntimeModelSetupCallbacks callbacks,
        string? sourceLanguageCode = null,
        string? targetLanguageCode = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(workspace);
        ArgumentNullException.ThrowIfNull(selections);
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(callbacks);

        RuntimeModelRequestOptions options = RuntimeModelRequestFactory.CreateOptions(selections);
        bool requiresVoiceClone = workspace.RuntimeModels is not null &&
            report.Stages.Any(s => s.StageName == StageNames.Tts);

        // Build one request per stage that needs provisioning (DownloadRequired or ImportRequired).
        // Cloud stages (CloudKeyMissing, ConsentRequired, etc.) are excluded — they resolve separately.
        IReadOnlyList<RuntimeModelRequest> requests = report.Stages
            .Where(s => s.Status is ReadinessState.DownloadRequired or ReadinessState.ImportRequired)
            .Select(s => BuildRequestForStage(s.StageName, options, requiresVoiceClone, sourceLanguageCode, targetLanguageCode))
            .OfType<RuntimeModelRequest>()
            .ToArray();

        bool hasSeparationRequest = requests.Any(r => r.Stage == RuntimeStage.Separation);

        RuntimeModelSetupResult result = await EnsureAvailableAsync(
            workspace,
            requests,
            callbacks,
            allowOptionalStageSkip: hasSeparationRequest,
            cancellationToken).ConfigureAwait(false);

        if (!result.IsReady)
        {
            return result;
        }

        bool lipSynthesisInRun = report.Stages.Any(static stage =>
            string.Equals(stage.StageName, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase));

        if (workspace.RuntimeModels is null || !lipSynthesisInRun)
        {
            return result;
        }

        return await RuntimeModelSetupWorkflow.EnsureManifestCompanionModelsAvailableAsync(
            workspace.RuntimeModels,
            LipSynthesisModelRequirements.CompanionManifestAliases,
            RuntimeStage.LipSynthesis,
            callbacks,
            cancellationToken).ConfigureAwait(false);
    }

    private static RuntimeModelRequest? BuildRequestForStage(
        string stageName,
        RuntimeModelRequestOptions options,
        bool requiresVoiceClone,
        string? sourceLang,
        string? targetLang) =>
        stageName switch
        {
            StageNames.Vad => RuntimeModelRequestFactory.CreateStageRequest(options, RuntimeStage.Vad),
            StageNames.Asr => RuntimeModelRequestFactory.CreateAsrRequest(options, sourceLang),
            StageNames.Diarization => RuntimeModelRequestFactory.CreateDiarizationRequest(options),
            StageNames.Translation => RuntimeModelRequestFactory.CreateTranslationRequest(
                                          options,
                                          sourceLang ?? string.Empty,
                                          targetLang ?? string.Empty),
            StageNames.Tts => RuntimeModelRequestFactory.CreateTtsRequest(options, requiresVoiceClone),
            StageNames.TextRefinementAsr => RuntimeModelRequestFactory.CreateTextRefinementRequest(options),
            StageNames.Separation => RuntimeModelRequestFactory.CreateSeparationRequest(options),
            StageNames.LipSync => RuntimeModelRequestFactory.CreateLipSyncRequest(options),
            StageNames.LipSynthesis => RuntimeModelRequestFactory.CreateLipSynthesisRequest(options),
            _ => null,
        };

    private static Task<RuntimeModelSetupResult> EnsureAvailableAsync(
        TranscriptWorkspace workspace,
        IReadOnlyList<RuntimeModelRequest> requests,
        RuntimeModelSetupCallbacks callbacks,
        bool allowOptionalStageSkip,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(workspace);

        return RuntimeModelSetupWorkflow.EnsureModelsAvailableAsync(
            workspace.RuntimeModels,
            requests,
            callbacks,
            allowOptionalStageSkip,
            cancellationToken);
    }
}
