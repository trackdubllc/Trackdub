using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// Tests for the guardrail logic inside
/// <see cref="SpeechAudioPreparationStageHandler.ProcessStageIfNeededAsync"/>.
///
/// <c>GetGuardrailFailure</c> is private static, so every case is driven through
/// <c>HandleAsync</c>. A <see cref="ControlledDecisionPlanner"/> forces
/// <c>RequiresProcessing = true</c> for all three stages so that the handler always
/// calls the processing service and then re-analyzes the output, giving the
/// guardrail a chance to fire.  The <see cref="FakeAudioQualityAnalyzer"/> queue
/// controls what the "before" and "after" analysis values look like.
///
/// Guardrail thresholds (from <see cref="AudioQualityPolicy"/>):
///   duration drift  &gt; 0.050 s
///   clipping increase &gt; 0.050 %
///   active RMS      &gt; -14.0 dBFS
///   speech-band worsening &gt; 2.0 dB
/// </summary>
public sealed class SpeechAudioPreparationGuardrailTests
{
    // ---------------------------------------------------------------
    // Helpers shared across all tests
    // ---------------------------------------------------------------

    private static AudioQualityMetrics GoodMetrics(SpeechAudioSourceKind sourceKind) =>
        new(
            DurationSeconds: 12.0d,
            PeakDbfs: -6.0d,
            RmsDbfs: -24.0d,
            ActiveRmsDbfs: -20.0d,
            Lufs: null,
            AudioQualityAnalysisConfidence.High,
            sourceKind,
            ClippedSamplePercent: 0.0d,
            NearSilencePercent: 0.0d,
            DcOffset: 0.0d,
            RumbleRatioDb: -30.0d,
            HissRatioDb: -30.0d,
            SpeechBandRatioDb: -3.0d,
            CrestFactorDb: 18.0d,
            DynamicRangeDb: 12.0d,
            NoiseFloorDbfs: -50.0d,
            SnrDb: 30.0d,
            AudioSnrConfidence.Reliable);

    private static AudioQualityAnalysisResult MakeAnalysis(
        AudioQualityMetrics metrics,
        SpeechAudioSourceKind sourceKind = SpeechAudioSourceKind.FullMix) =>
        new(
            "virtual.wav",
            metrics,
            AudioQualityAnalysisThresholds.ForSource(sourceKind),
            [],
            []);

    private static MediaAsset CreateMediaAsset() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            DateTimeOffset.UtcNow,
            "mp4",
            DurationSeconds: 12.0d,
            HasAudio: true,
            HasVideo: true,
            DateTimeOffset.UtcNow);

    private static ProjectArtifact CreateNormalizedArtifact(MediaAsset mediaAsset) =>
        new(
            Guid.NewGuid(),
            mediaAsset.ProjectId,
            mediaAsset.Id,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "sha256",
            1024,
            DurationSeconds: 12.0d,
            SampleRate: 48000,
            ChannelCount: 1,
            DateTimeOffset.UtcNow);

    /// <summary>
    /// Builds the handler wired with a <see cref="ControlledDecisionPlanner"/> that
    /// forces RequiresProcessing = true for all stages using the Hiss-triggering
    /// FullMixAsrLight profile (which carries a non-empty FilterChain).
    /// </summary>
    private static SpeechAudioPreparationStageHandler BuildHandlerWithForcedProcessing(
        FakeAudioQualityAnalyzer analyzer,
        FakeSpeechAudioProcessingService processor,
        FakeMediaAssetRepository mediaRepository)
    {
        // Build a filter selection that makes RequiresProcessing = true:
        // profile must not be "none", must have a non-empty FilterChain, must be
        // IsAutoSelectable=true, and IsBenchmarkOnly=false.
        SpeechAudioFilterSelection filterSelection =
            SpeechAudioProcessingProfileCatalog.BuildFilterSelection(
                SpeechAudioProcessingProfileCatalog.FullMixAsrLightProfileId,
                [AudioQualityDefectKind.Hiss]);

        var planner = new ControlledDecisionPlanner(filterSelection);

        return new SpeechAudioPreparationStageHandler(
            analyzer,
            planner,
            processor,
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            mediaRepository,
            new FakeProjectStageRunStore());
    }

    // ---------------------------------------------------------------
    // Test: no processing requested → processing service never called
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenRequiresProcessingFalse_DoesNotCallProcessingService()
    {
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        // Use the real planner with a clean-audio analysis → NoneProfileId →
        // RequiresProcessing = false for all three stages.
        var analyzer = new FakeAudioQualityAnalyzer();
        var processor = new FakeSpeechAudioProcessingService();

        var handler = new SpeechAudioPreparationStageHandler(
            analyzer,
            new SpeechAudioPreparationPlanner(),
            processor,
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            mediaRepository,
            new FakeProjectStageRunStore());

        await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.Empty(processor.Requests);
    }

    // ---------------------------------------------------------------
    // Test: clean processing result → no FallbackReason on any stage
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenProcessedAudioIsClean_FallbackReasonIsNull()
    {
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics sourceMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);

        // The ControlledDecisionPlanner uses FullMixAsrLightProfileId whose FilterChain
        // contains "afftdn", triggering the SNR-improvement guardrail check.  The check
        // fires when afterSnr - beforeSnr < DenoiseMinimumSnrImprovementDb (2.0 dB).
        // Supply post-processing metrics with SnrDb improved by 5 dB (30 → 35) so the
        // denoise guardrail does NOT fire, while all other metrics remain healthy.
        AudioQualityMetrics processedMetrics = sourceMetrics with { SnrDb = sourceMetrics.SnrDb + 5.0d };

        var analyzer = new FakeAudioQualityAnalyzer();
        // 1 initial full-mix analysis + 3 post-processing re-analyses.
        analyzer.QueueResult(MakeAnalysis(sourceMetrics));
        analyzer.QueueResult(MakeAnalysis(processedMetrics));
        analyzer.QueueResult(MakeAnalysis(processedMetrics));
        analyzer.QueueResult(MakeAnalysis(processedMetrics));

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        TranscriptAudioRoutingPlan result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.Null(result.VadDecision.FallbackReason);
        Assert.Null(result.AsrDecision.FallbackReason);
        Assert.Null(result.DiarizationDecision.FallbackReason);
    }

    // ---------------------------------------------------------------
    // Test: duration drift guardrail
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenDurationDriftExceedsThreshold_FallbackReasonContainsDuration()
    {
        // Threshold: drift > 0.050s → guardrail fires.
        // We push drift = 0.2s (well above threshold).
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics goodMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);

        // Post-processing result has a significantly different duration.
        AudioQualityMetrics driftedMetrics = goodMetrics with
        {
            DurationSeconds = goodMetrics.DurationSeconds + 0.2d
        };

        var analyzer = new FakeAudioQualityAnalyzer();
        // Queue 4 results: one initial source + three post-processing.
        // All three post-processing analyses return drifted metrics so that
        // every stage trips the guardrail (making assertions deterministic).
        analyzer.QueueResult(MakeAnalysis(goodMetrics));
        analyzer.QueueResult(MakeAnalysis(driftedMetrics));
        analyzer.QueueResult(MakeAnalysis(driftedMetrics));
        analyzer.QueueResult(MakeAnalysis(driftedMetrics));

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        TranscriptAudioRoutingPlan result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        // All three stages should have been guardrail-rejected.
        Assert.NotNull(result.AsrDecision.FallbackReason);
        Assert.Contains("duration", result.AsrDecision.FallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.VadDecision.FallbackReason);
        Assert.Contains("duration", result.VadDecision.FallbackReason, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(result.DiarizationDecision.FallbackReason);
        Assert.Contains("duration", result.DiarizationDecision.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // Test: clipping increase guardrail
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenClippingIncreasesAboveThreshold_FallbackReasonContainsClipping()
    {
        // Threshold: increase > 0.050% → guardrail fires.
        // Before: 0.0%, After: 0.5% → increase = 0.5% > 0.050%.
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics goodMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);
        AudioQualityMetrics clippedMetrics = goodMetrics with { ClippedSamplePercent = 0.5d };

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(MakeAnalysis(goodMetrics));       // source analysis
        analyzer.QueueResult(MakeAnalysis(clippedMetrics));   // VAD post-process
        analyzer.QueueResult(MakeAnalysis(clippedMetrics));   // ASR post-process
        analyzer.QueueResult(MakeAnalysis(clippedMetrics));   // Diarization post-process

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        TranscriptAudioRoutingPlan result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.AsrDecision.FallbackReason);
        Assert.Contains("clipping", result.AsrDecision.FallbackReason, StringComparison.OrdinalIgnoreCase);
    }

    // ---------------------------------------------------------------
    // Test: active RMS too hot guardrail
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenActiveRmsExceedsThreshold_FallbackReasonIsSet()
    {
        // Threshold: processed ActiveRmsDbfs > -14.0 dBFS → guardrail fires.
        // Use -10.0 dBFS (above threshold).
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics goodMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);
        AudioQualityMetrics hotMetrics = goodMetrics with { ActiveRmsDbfs = -10.0d };

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(MakeAnalysis(goodMetrics));   // source analysis
        analyzer.QueueResult(MakeAnalysis(hotMetrics));    // VAD post-process
        analyzer.QueueResult(MakeAnalysis(hotMetrics));    // ASR post-process
        analyzer.QueueResult(MakeAnalysis(hotMetrics));    // Diarization post-process

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        TranscriptAudioRoutingPlan result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.AsrDecision.FallbackReason);
        // When the guardrail fires, the handler sets RequiresProcessing = false
        // and returns the raw artifact, so the routing plan artifact should
        // resolve back to the original normalized source.
        Assert.Equal(normalized.RelativePath, result.AsrAudioArtifact.RelativePath);
    }

    // ---------------------------------------------------------------
    // Test: speech-band ratio worsened guardrail
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenSpeechBandRatioWorsens_FallbackReasonIsSet()
    {
        // Threshold: worsening = before - after > 2.0 dB → guardrail fires.
        // Before: SpeechBandRatioDb = -3.0, After: -6.0 → worsening = 3.0 > 2.0.
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics goodMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);
        AudioQualityMetrics worsenedMetrics = goodMetrics with { SpeechBandRatioDb = -6.0d };

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(MakeAnalysis(goodMetrics));       // source analysis
        analyzer.QueueResult(MakeAnalysis(worsenedMetrics));  // VAD post-process
        analyzer.QueueResult(MakeAnalysis(worsenedMetrics));  // ASR post-process
        analyzer.QueueResult(MakeAnalysis(worsenedMetrics));  // Diarization post-process

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        TranscriptAudioRoutingPlan result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.AsrDecision.FallbackReason);
        Assert.NotNull(result.VadDecision.FallbackReason);
        Assert.NotNull(result.DiarizationDecision.FallbackReason);
    }

    // ---------------------------------------------------------------
    // Test: guardrail fires → processed artifact is NOT persisted
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenGuardrailFires_ProcessedArtifactIsNotPersisted()
    {
        // When the guardrail fires the handler must discard the processed output
        // and must NOT write a SpeechProcessedAudio artifact to the repository.
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics goodMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);
        // Duration drift of 1.0 s → well beyond 0.050 s threshold.
        AudioQualityMetrics driftedMetrics = goodMetrics with { DurationSeconds = goodMetrics.DurationSeconds + 1.0d };

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(MakeAnalysis(goodMetrics));
        analyzer.QueueResult(MakeAnalysis(driftedMetrics));
        analyzer.QueueResult(MakeAnalysis(driftedMetrics));
        analyzer.QueueResult(MakeAnalysis(driftedMetrics));

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(
            mediaRepository.Artifacts,
            a => a.Kind == ArtifactKind.SpeechProcessedAudio);
    }

    // ---------------------------------------------------------------
    // Test: guardrail fires → ProcessedAnalysis is populated on decision
    // ---------------------------------------------------------------

    [Fact]
    public async Task HandleAsync_WhenGuardrailFires_ProcessedAnalysisIsRetainedOnDecision()
    {
        // Even when the guardrail discards the processed output, the decision
        // record should still carry the ProcessedAnalysis for audit/logging.
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateNormalizedArtifact(mediaAsset);
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);

        AudioQualityMetrics goodMetrics = GoodMetrics(SpeechAudioSourceKind.FullMix);
        AudioQualityMetrics hotMetrics = goodMetrics with { ActiveRmsDbfs = -5.0d };

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(MakeAnalysis(goodMetrics));
        analyzer.QueueResult(MakeAnalysis(hotMetrics));
        analyzer.QueueResult(MakeAnalysis(hotMetrics));
        analyzer.QueueResult(MakeAnalysis(hotMetrics));

        var processor = new FakeSpeechAudioProcessingService();

        SpeechAudioPreparationStageHandler handler =
            BuildHandlerWithForcedProcessing(analyzer, processor, mediaRepository);

        TranscriptAudioRoutingPlan result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(
                mediaAsset.ProjectId,
                mediaAsset,
                normalized,
                VocalStemArtifact: null,
                [normalized]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.AsrDecision.FallbackReason);
        Assert.NotNull(result.AsrDecision.ProcessedAnalysis);
        Assert.Equal(-5.0d, result.AsrDecision.ProcessedAnalysis!.Metrics.ActiveRmsDbfs, precision: 3);
    }

    // ---------------------------------------------------------------
    // Private nested test double
    // ---------------------------------------------------------------

    /// <summary>
    /// A planner that always returns a <see cref="SpeechAudioPreparationPlan"/> whose
    /// three stage decisions all have <c>RequiresProcessing = true</c>, using the
    /// supplied <paramref name="filterSelection"/>.  This bypasses the real planner
    /// logic and gives tests full control over whether processing is attempted.
    /// </summary>
    private sealed class ControlledDecisionPlanner(
        SpeechAudioFilterSelection filterSelection) : ISpeechAudioPreparationPlanner
    {
        public SpeechAudioPreparationPlan Plan(SpeechAudioPreparationPlanningRequest request)
        {
            // Build a source analysis from the full-mix analysis that was passed in
            // (from the queued analyzer result that HandleAsync fetched before calling Plan).
            AudioQualityAnalysisResult sourceAnalysis = request.FullMixAnalysis;

            SpeechAudioStageDecision MakeDecision(SpeechPipelineStageKind stage) =>
                new(
                    stage,
                    SpeechAudioSourceKind.FullMix,
                    filterSelection.ProfileId,
                    filterSelection.ProfileVersion,
                    filterSelection.CatalogVersion,
                    filterSelection.FilterChain,
                    filterSelection.ProfileHash,
                    RequiresProcessing: true,
                    TriggeredDefects: [AudioQualityDefectKind.Hiss]);

            return new SpeechAudioPreparationPlan(
                SelectedSourceKind: SpeechAudioSourceKind.FullMix,
                SelectedSourceRejected: false,
                SourceRejectionReason: null,
                SelectedSourceAnalysis: sourceAnalysis,
                FullMixAnalysis: sourceAnalysis,
                VocalStemAnalysis: null,
                VadDecision: MakeDecision(SpeechPipelineStageKind.Vad),
                AsrDecision: MakeDecision(SpeechPipelineStageKind.Asr),
                DiarizationDecision: MakeDecision(SpeechPipelineStageKind.Diarization));
        }
    }
}
