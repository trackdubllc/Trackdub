using Trackdub.Application.Artifacts;
using Trackdub.Application.Logging;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Projects;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed class SpeechAudioPreparationStageHandler(
    IAudioQualityAnalyzer audioQualityAnalyzer,
    ISpeechAudioPreparationPlanner preparationPlanner,
    ISpeechAudioProcessingService processingService,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    IProjectStageRunStore stageRunStore,
    IApplicationLogger? logger = null,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    PipelineDegradationWriter? degradationWriter = null)
{
    private readonly IAudioQualityAnalyzer audioQualityAnalyzer = audioQualityAnalyzer ?? throw new ArgumentNullException(nameof(audioQualityAnalyzer));
    private readonly ISpeechAudioPreparationPlanner preparationPlanner = preparationPlanner ?? throw new ArgumentNullException(nameof(preparationPlanner));
    private readonly ISpeechAudioProcessingService processingService = processingService ?? throw new ArgumentNullException(nameof(processingService));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));

    public async Task<TranscriptAudioRoutingPlan> HandleAsync(
        SpeechAudioPreparationStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        (StageRunRecord _, TranscriptAudioRoutingPlan routingPlan) = await StageRunHelper.RunStageAsync(
                stageRunStore,
                request.ProjectId,
                StageNames.AudioPreparation,
                null,
                async (stageRun, ct) =>
                {
                    AudioQualityAnalysisResult fullMixAnalysis = await AnalyzeAsync(
                        request.NormalizedAudioArtifact,
                        SpeechAudioSourceKind.FullMix,
                        ct).ConfigureAwait(false);
                    AudioQualityAnalysisResult? vocalAnalysis = request.VocalStemArtifact is null
                        ? null
                        : await AnalyzeAsync(
                            request.VocalStemArtifact,
                            SpeechAudioSourceKind.VocalStem,
                            ct).ConfigureAwait(false);

                    SpeechAudioPreparationPlan preparationPlan = preparationPlanner.Plan(
                        new SpeechAudioPreparationPlanningRequest(
                            request.MediaAsset,
                            request.NormalizedAudioArtifact,
                            request.VocalStemArtifact,
                            fullMixAnalysis,
                            vocalAnalysis));

                    ProjectArtifact selectedSourceArtifact = preparationPlan.SelectedSourceKind is SpeechAudioSourceKind.VocalStem && request.VocalStemArtifact is not null
                        ? request.VocalStemArtifact
                        : request.NormalizedAudioArtifact;
                    Guid analysisArtifactId = Guid.NewGuid();
                    var processedArtifacts = new Dictionary<Guid, ProjectArtifact>();

                    if (preparationPlan.SelectedSourceRejected && !string.IsNullOrWhiteSpace(preparationPlan.SourceRejectionReason))
                    {
                        logger?.LogWarning(
                            "Vocal stem rejected for transcript audio preparation: {SourceRejectionReason}",
                            preparationPlan.SourceRejectionReason);
                    }

                    if (logger is not null && preparationPlan.VocalStemAnalysis is not null)
                    {
                        // Diagnostics: surface the actual quality metrics vs thresholds that drive
                        // vocal-stem selection, so rejection decisions can be evaluated with real numbers.
                        logger.LogInformation(
                            $"Audio prep diagnostics for project {request.MediaAsset.ProjectId}: " +
                            $"selectedSource={preparationPlan.SelectedSourceKind} rejected={preparationPlan.SelectedSourceRejected} " +
                            $"mediaDuration={request.MediaAsset.DurationSeconds:F2}s. " +
                            $"VocalStem {{ {FormatQualityDiagnostics(preparationPlan.VocalStemAnalysis)} }}. " +
                            $"FullMix {{ {FormatQualityDiagnostics(preparationPlan.FullMixAnalysis)} }}.");
                    }

                    SpeechAudioStageDecision vadDecision = await ProcessStageIfNeededAsync(
                        request,
                        stageRun,
                        selectedSourceArtifact,
                        preparationPlan.VadDecision,
                        preparationPlan.SelectedSourceAnalysis,
                        analysisArtifactId,
                        processedArtifacts,
                        ct).ConfigureAwait(false);
                    SpeechAudioStageDecision asrDecision = await ProcessStageIfNeededAsync(
                        request,
                        stageRun,
                        selectedSourceArtifact,
                        preparationPlan.AsrDecision,
                        preparationPlan.SelectedSourceAnalysis,
                        analysisArtifactId,
                        processedArtifacts,
                        ct).ConfigureAwait(false);
                    SpeechAudioStageDecision diarizationDecision = await ProcessStageIfNeededAsync(
                        request,
                        stageRun,
                        selectedSourceArtifact,
                        preparationPlan.DiarizationDecision,
                        preparationPlan.SelectedSourceAnalysis,
                        analysisArtifactId,
                        processedArtifacts,
                        ct).ConfigureAwait(false);

                    var audit = SpeechAudioPreparationAudit.Create(
                        request,
                        preparationPlan,
                        vadDecision,
                        asrDecision,
                        diarizationDecision);

                    string analysisRelativePath = ProjectArtifactPaths.GetAudioQualityAnalysisRelativePath(stageRun.Id);
                    await artifactStore.WriteJsonAsync(analysisRelativePath, audit, ct).ConfigureAwait(false);
                    FileFingerprint analysisFingerprint = await fileFingerprintService
                        .ComputeAsync(artifactStore.GetPath(analysisRelativePath), ct)
                        .ConfigureAwait(false);

                    ProjectArtifact analysisArtifact = CreateAnalysisArtifact(
                        request,
                        stageRun,
                        preparationPlan,
                        analysisArtifactId,
                        analysisRelativePath,
                        analysisFingerprint);

                    foreach (ProjectArtifact processedArtifact in processedArtifacts.Values)
                    {
                        await mediaAssetRepository.SaveArtifactAsync(processedArtifact, ct).ConfigureAwait(false);
                    }

                    await mediaAssetRepository.SaveArtifactAsync(analysisArtifact, ct).ConfigureAwait(false);

                    return new TranscriptAudioRoutingPlan(
                        ResolveArtifact(selectedSourceArtifact, vadDecision, processedArtifacts),
                        ResolveArtifact(selectedSourceArtifact, asrDecision, processedArtifacts),
                        ResolveArtifact(selectedSourceArtifact, diarizationDecision, processedArtifacts),
                        preparationPlan.SelectedSourceKind,
                        analysisArtifact,
                        vadDecision,
                        asrDecision,
                        diarizationDecision);
                },
                "Speech audio preparation canceled.",
                cancellationToken,
                runtimePlanningPreferences,
                logger)
            .ConfigureAwait(false);

        return routingPlan;
    }

    private async Task<AudioQualityAnalysisResult> AnalyzeAsync(
        ProjectArtifact artifact,
        SpeechAudioSourceKind sourceKind,
        CancellationToken cancellationToken)
    {
        AudioQualityAnalysisResult analysis = await audioQualityAnalyzer
            .AnalyzeAsync(
                new AudioQualityAnalysisRequest(
                    artifactStore.GetPath(artifact.RelativePath),
                    sourceKind,
                    AudioQualityAnalysisThresholds.ForSource(sourceKind)),
                cancellationToken)
            .ConfigureAwait(false);

        logger?.LogDebug(
            $"Audio quality analysis ({sourceKind}): peak={analysis.Metrics.PeakDbfs:F1}dBFS " +
            $"activeRms={analysis.Metrics.ActiveRmsDbfs:F1}dBFS snr={FormatNullable(analysis.Metrics.SnrDb)} " +
            $"defects={string.Join(',', analysis.TriggeredDefects)}");
        return analysis with
        {
            AudioPath = artifact.RelativePath
        };
    }

    private async Task<SpeechAudioStageDecision> ProcessStageIfNeededAsync(
        SpeechAudioPreparationStageRequest request,
        StageRunRecord stageRun,
        ProjectArtifact selectedSourceArtifact,
        SpeechAudioStageDecision decision,
        AudioQualityAnalysisResult sourceAnalysis,
        Guid analysisArtifactId,
        IDictionary<Guid, ProjectArtifact> processedArtifacts,
        CancellationToken cancellationToken)
    {
        if (!decision.RequiresProcessing)
        {
            return decision;
        }

        logger?.LogWarning(
            $"Speech audio preparation selected profile '{decision.ProfileId}' for {decision.Stage} from {decision.SourceKind}.");

        string stageName = decision.Stage.ToString().ToLowerInvariant();
        string processedRelativePath = ProjectArtifactPaths.GetSpeechProcessedAudioRelativePath(stageRun.Id, stageName);
        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(processedRelativePath));

        SpeechAudioProcessingResult processingResult = await processingService
            .ProcessAsync(
                new SpeechAudioProcessingRequest(
                    artifactStore.GetPath(selectedSourceArtifact.RelativePath),
                    tx.TemporaryPath,
                    new SpeechAudioFilterSelection(
                        decision.ProfileId,
                        decision.ProfileVersion,
                        decision.CatalogVersion,
                        decision.FilterChain,
                        decision.ProfileHash,
                        IsAutoSelectable: true,
                        IsBenchmarkOnly: false)),
                cancellationToken)
            .ConfigureAwait(false);

        AudioQualityAnalysisResult processedAnalysis = await audioQualityAnalyzer
            .AnalyzeAsync(
                new AudioQualityAnalysisRequest(
                    processingResult.OutputPath,
                    decision.SourceKind,
                    AudioQualityAnalysisThresholds.ForSource(decision.SourceKind)),
                cancellationToken)
            .ConfigureAwait(false);
        processedAnalysis = processedAnalysis with
        {
            AudioPath = processedRelativePath
        };

        string? guardrailFailure = GetGuardrailFailure(decision, sourceAnalysis, processedAnalysis);
        if (guardrailFailure is not null)
        {
            logger?.LogWarning(
                "Discarding processed audio for {Stage}: {GuardrailFailure}",
                decision.Stage,
                guardrailFailure);

            if (degradationWriter is not null)
            {
                try
                {
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            StageNames.AudioPreparation,
                            "AUDIO_PREPARATION_GUARDRAIL_FAILURE",
                            $"Processed audio for {decision.Stage} failed quality guardrails: {guardrailFailure}",
                            Detail: $"Profile: {decision.ProfileId}, Source: {decision.SourceKind}",
                            SelectedFallback: "Original audio",
                            RecommendedAction: null,
                            DateTimeOffset.UtcNow,
                            stageRun.Id),
                        request.ProjectId,
                        request.MediaAsset.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    logger?.LogWarning("Failed to write degradation record for audio preparation guardrail failure.", ex);
                }
            }
            return decision with
            {
                RequiresProcessing = false,
                FallbackReason = guardrailFailure,
                ProcessedAnalysis = processedAnalysis
            };
        }

        await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);
        FileFingerprint fingerprint = await fileFingerprintService
            .ComputeAsync(artifactStore.GetPath(processedRelativePath), cancellationToken)
            .ConfigureAwait(false);

        ProjectArtifact artifact = CreateProcessedArtifact(
            request,
            stageRun,
            selectedSourceArtifact,
            processedRelativePath,
            fingerprint,
            processingResult,
            decision,
            analysisArtifactId);
        processedArtifacts[artifact.Id] = artifact;

        return decision with
        {
            OutputArtifactId = artifact.Id,
            OutputRelativePath = artifact.RelativePath,
            ProcessedAnalysis = processedAnalysis
        };
    }

    private static string? GetGuardrailFailure(
        SpeechAudioStageDecision decision,
        AudioQualityAnalysisResult before,
        AudioQualityAnalysisResult after)
    {
        double durationDrift = Math.Abs(after.Metrics.DurationSeconds - before.Metrics.DurationSeconds);
        if (durationDrift > AudioQualityPolicy.ProcessedDurationDriftRejectSeconds)
        {
            return $"duration drift {durationDrift:F3}s exceeded {AudioQualityPolicy.ProcessedDurationDriftRejectSeconds:F3}s";
        }

        double clippingIncrease = after.Metrics.ClippedSamplePercent - before.Metrics.ClippedSamplePercent;
        if (clippingIncrease > AudioQualityPolicy.ProcessedClippingIncreaseRejectPercent)
        {
            return $"clipping increased by {clippingIncrease:F3}%";
        }

        if (after.Metrics.ActiveRmsDbfs > AudioQualityPolicy.ProcessedActiveRmsRejectDbfs)
        {
            return $"active RMS {after.Metrics.ActiveRmsDbfs:F1} dBFS is too hot";
        }

        double speechBandWorsening = before.Metrics.SpeechBandRatioDb - after.Metrics.SpeechBandRatioDb;
        if (speechBandWorsening > AudioQualityPolicy.ProcessedSpeechBandWorsenRejectDb)
        {
            return $"speech-band ratio worsened by {speechBandWorsening:F1} dB";
        }

        bool denoiseProfile = decision.ProfileId.Contains("denoise", StringComparison.OrdinalIgnoreCase) ||
                              decision.FilterChain.Contains("afftdn", StringComparison.OrdinalIgnoreCase);
        if (denoiseProfile &&
            before.Metrics.SnrDb is double beforeSnr &&
            after.Metrics.SnrDb is double afterSnr &&
            before.Metrics.SnrConfidence is AudioSnrConfidence.Reliable &&
            after.Metrics.SnrConfidence is not AudioSnrConfidence.Unavailable &&
            afterSnr - beforeSnr < AudioQualityPolicy.DenoiseMinimumSnrImprovementDb)
        {
            return $"SNR improved by only {afterSnr - beforeSnr:F1} dB";
        }

        return null;
    }

    private static ProjectArtifact ResolveArtifact(
        ProjectArtifact rawArtifact,
        SpeechAudioStageDecision decision,
        IReadOnlyDictionary<Guid, ProjectArtifact> processedArtifacts) =>
        decision.OutputArtifactId is Guid outputArtifactId && processedArtifacts.TryGetValue(outputArtifactId, out ProjectArtifact? processedArtifact)
            ? processedArtifact
            : rawArtifact;

    private static ProjectArtifact CreateAnalysisArtifact(
        SpeechAudioPreparationStageRequest request,
        StageRunRecord stageRun,
        SpeechAudioPreparationPlan plan,
        Guid artifactId,
        string relativePath,
        FileFingerprint fingerprint) =>
        new(
            artifactId,
            request.ProjectId,
            request.MediaAsset.Id,
            ArtifactKind.AudioQualityAnalysis,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            DurationSeconds: null,
            SampleRate: null,
            ChannelCount: null,
            DateTimeOffset.UtcNow,
            StageRunId: stageRun.Id,
            Provenance: $"audio-quality-analysis:policy={AudioQualityPolicy.AnalyzerPolicyVersion};catalog={SpeechAudioProcessingProfileCatalog.CatalogVersion};selectedSource={plan.SelectedSourceKind};selectedSourceRejected={plan.SelectedSourceRejected.ToString().ToLowerInvariant()}");

    private static ProjectArtifact CreateProcessedArtifact(
        SpeechAudioPreparationStageRequest request,
        StageRunRecord stageRun,
        ProjectArtifact sourceArtifact,
        string relativePath,
        FileFingerprint fingerprint,
        SpeechAudioProcessingResult result,
        SpeechAudioStageDecision decision,
        Guid analysisArtifactId) =>
        new(
            Guid.NewGuid(),
            request.ProjectId,
            request.MediaAsset.Id,
            ArtifactKind.SpeechProcessedAudio,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            result.DurationSeconds,
            result.SampleRate,
            result.ChannelCount,
            DateTimeOffset.UtcNow,
            StageRunId: stageRun.Id,
            Provenance: $"speech-processing:source={sourceArtifact.Id:D};stage={decision.Stage.ToString().ToLowerInvariant()};profile={decision.ProfileId};version={decision.ProfileVersion};hash={decision.ProfileHash};analysis={analysisArtifactId:D}");

    private static string FormatNullable(double? value) =>
        value is null ? "n/a" : $"{value.Value:F1}dB";

    private static string FormatQualityDiagnostics(AudioQualityAnalysisResult analysis)
    {
        AudioQualityMetrics m = analysis.Metrics;
        AudioQualityAnalysisThresholds t = analysis.Thresholds;
        string defects = analysis.TriggeredDefects.Count == 0
            ? "none"
            : string.Join("+", analysis.TriggeredDefects);
        return
            $"rumble={m.RumbleRatioDb:F1}dB(>{t.RumbleRatioDb:F1} flags), " +
            $"hiss={m.HissRatioDb:F1}dB(>{t.HissRatioDb:F1} flags), " +
            $"snr={FormatNullable(m.SnrDb)}({m.SnrConfidence}, <{t.LowSnrDb:F1} flags), " +
            $"speechBand={m.SpeechBandRatioDb:F1}dB(<{t.PoorSpeechBandRatioDb:F1} flags, unusable<{AudioQualityPolicy.UnusableSpeechBandRatioDb:F1}), " +
            $"activeRms={m.ActiveRmsDbfs:F1}dBFS(unusable<{AudioQualityPolicy.UnusableActiveRmsDbfs:F1}), " +
            $"clip={m.ClippedSamplePercent:F3}%, dur={m.DurationSeconds:F2}s, conf={m.AnalysisConfidence}, defects=[{defects}]";
    }
}

public sealed record SpeechAudioPreparationStageRequest(
    Guid ProjectId,
    MediaAsset MediaAsset,
    ProjectArtifact NormalizedAudioArtifact,
    ProjectArtifact? VocalStemArtifact,
    IReadOnlyList<ProjectArtifact> ExistingArtifacts);

public sealed record SpeechAudioPreparationAudit(
    Guid ProjectId,
    Guid MediaAssetId,
    string AnalyzerPolicyVersion,
    string ProfileCatalogVersion,
    SpeechAudioSourceAudit FullMix,
    SpeechAudioSourceAudit? VocalStem,
    SpeechAudioSourceKind SelectedSourceKind,
    bool SelectedSourceRejected,
    string? SourceRejectionReason,
    IReadOnlyList<SpeechAudioDecisionAudit> Decisions)
{
    public static SpeechAudioPreparationAudit Create(
        SpeechAudioPreparationStageRequest request,
        SpeechAudioPreparationPlan plan,
        params SpeechAudioStageDecision[] decisions) =>
        new(
            request.ProjectId,
            request.MediaAsset.Id,
            AudioQualityPolicy.AnalyzerPolicyVersion,
            SpeechAudioProcessingProfileCatalog.CatalogVersion,
            SpeechAudioSourceAudit.FromArtifact(request.NormalizedAudioArtifact, plan.FullMixAnalysis),
            request.VocalStemArtifact is null || plan.VocalStemAnalysis is null
                ? null
                : SpeechAudioSourceAudit.FromArtifact(request.VocalStemArtifact, plan.VocalStemAnalysis),
            plan.SelectedSourceKind,
            plan.SelectedSourceRejected,
            plan.SourceRejectionReason,
            decisions.Select(SpeechAudioDecisionAudit.FromDecision).ToArray());
}

public sealed record SpeechAudioSourceAudit(
    Guid SourceArtifactId,
    string SourceRelativePath,
    string SourceSha256,
    ArtifactKind SourceKind,
    AudioQualityAnalysisResult Analysis)
{
    public static SpeechAudioSourceAudit FromArtifact(ProjectArtifact artifact, AudioQualityAnalysisResult analysis) =>
        new(artifact.Id, artifact.RelativePath, artifact.Sha256, artifact.Kind, analysis);
}

public sealed record SpeechAudioDecisionAudit(
    SpeechPipelineStageKind Stage,
    string ProfileId,
    int ProfileVersion,
    string ProfileHash,
    string FilterChain,
    bool RequiresProcessing,
    IReadOnlyList<AudioQualityDefectKind> TriggeredDefects,
    Guid? OutputArtifactId,
    string? OutputRelativePath,
    string? FallbackReason,
    AudioQualityAnalysisResult? ProcessedAnalysis)
{
    public static SpeechAudioDecisionAudit FromDecision(SpeechAudioStageDecision decision) =>
        new(
            decision.Stage,
            decision.ProfileId,
            decision.ProfileVersion,
            decision.ProfileHash,
            decision.FilterChain,
            decision.RequiresProcessing,
            decision.TriggeredDefects,
            decision.OutputArtifactId,
            decision.OutputRelativePath,
            decision.FallbackReason,
            decision.ProcessedAnalysis);
}
