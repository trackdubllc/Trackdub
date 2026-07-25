using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class SpeechAudioPreparationTests
{
    [Fact]
    public void Planner_keeps_clean_vocal_stem_raw()
    {
        var planner = new SpeechAudioPreparationPlanner();
        MediaAsset mediaAsset = CreateMediaAsset();
        AudioQualityAnalysisResult fullMix = CreateAnalysis(SpeechAudioSourceKind.FullMix, [AudioQualityDefectKind.Hiss]);
        AudioQualityAnalysisResult vocals = CreateAnalysis(SpeechAudioSourceKind.VocalStem, []);

        SpeechAudioPreparationPlan plan = planner.Plan(new SpeechAudioPreparationPlanningRequest(
            mediaAsset,
            CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath),
            CreateArtifact(mediaAsset, ArtifactKind.Vocals, ProjectArtifactPaths.GetStemVocalsRelativePath(Guid.NewGuid())),
            fullMix,
            vocals));

        Assert.Equal(SpeechAudioSourceKind.VocalStem, plan.SelectedSourceKind);
        Assert.False(plan.AsrDecision.RequiresProcessing);
        Assert.Equal(SpeechAudioProcessingProfileCatalog.NoneProfileId, plan.AsrDecision.ProfileId);
    }

    [Fact]
    public void Planner_rejects_vocal_stem_with_poor_speech_band_and_uses_full_mix()
    {
        var planner = new SpeechAudioPreparationPlanner();
        MediaAsset mediaAsset = CreateMediaAsset();
        AudioQualityAnalysisResult fullMix = CreateAnalysis(SpeechAudioSourceKind.FullMix, []);
        AudioQualityAnalysisResult vocals = CreateAnalysis(SpeechAudioSourceKind.VocalStem, [AudioQualityDefectKind.PoorSpeechBand]);

        SpeechAudioPreparationPlan plan = planner.Plan(new SpeechAudioPreparationPlanningRequest(
            mediaAsset,
            CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath),
            CreateArtifact(mediaAsset, ArtifactKind.Vocals, ProjectArtifactPaths.GetStemVocalsRelativePath(Guid.NewGuid())),
            fullMix,
            vocals));

        Assert.Equal(SpeechAudioSourceKind.FullMix, plan.SelectedSourceKind);
        Assert.True(plan.SelectedSourceRejected);
        Assert.Contains("PoorSpeechBand", plan.SourceRejectionReason, StringComparison.Ordinal);
        Assert.Equal(SpeechAudioSourceKind.FullMix, plan.AsrDecision.SourceKind);
    }

    [Fact]
    public void Planner_remediates_rumble_on_vocal_stem_instead_of_rejecting()
    {
        var planner = new SpeechAudioPreparationPlanner();
        MediaAsset mediaAsset = CreateMediaAsset();
        AudioQualityAnalysisResult fullMix = CreateAnalysis(SpeechAudioSourceKind.FullMix, []);
        AudioQualityAnalysisResult vocals = CreateAnalysis(SpeechAudioSourceKind.VocalStem, [AudioQualityDefectKind.Rumble]);

        SpeechAudioPreparationPlan plan = planner.Plan(new SpeechAudioPreparationPlanningRequest(
            mediaAsset,
            CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath),
            CreateArtifact(mediaAsset, ArtifactKind.Vocals, ProjectArtifactPaths.GetStemVocalsRelativePath(Guid.NewGuid())),
            fullMix,
            vocals));

        Assert.Equal(SpeechAudioSourceKind.VocalStem, plan.SelectedSourceKind);
        Assert.False(plan.SelectedSourceRejected);
        Assert.Equal(SpeechAudioProcessingProfileCatalog.VocalRumbleCutProfileId, plan.AsrDecision.ProfileId);
        Assert.True(plan.AsrDecision.RequiresProcessing);
    }

    [Fact]
    public void Planner_selects_stage_specific_full_mix_asr_profile_for_hiss()
    {
        var planner = new SpeechAudioPreparationPlanner();
        MediaAsset mediaAsset = CreateMediaAsset();
        AudioQualityAnalysisResult fullMix = CreateAnalysis(SpeechAudioSourceKind.FullMix, [AudioQualityDefectKind.Hiss]);

        SpeechAudioPreparationPlan plan = planner.Plan(new SpeechAudioPreparationPlanningRequest(
            mediaAsset,
            CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath),
            VocalStemArtifact: null,
            fullMix,
            VocalStemAnalysis: null));

        Assert.Equal(SpeechAudioProcessingProfileCatalog.FullMixAsrLightProfileId, plan.AsrDecision.ProfileId);
        Assert.Contains("lowpass=f=8000", plan.AsrDecision.FilterChain, StringComparison.Ordinal);
        Assert.False(plan.VadDecision.RequiresProcessing);
        Assert.False(plan.DiarizationDecision.RequiresProcessing);
    }

    [Fact]
    public async Task StageHandler_discards_processed_output_when_guardrail_fails()
    {
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath);
        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(CreateAnalysis(SpeechAudioSourceKind.FullMix, [AudioQualityDefectKind.Hiss]));
        analyzer.QueueResult(CreateAnalysis(SpeechAudioSourceKind.FullMix, []) with
        {
            Metrics = CreateMetrics(SpeechAudioSourceKind.FullMix) with { ActiveRmsDbfs = -10.0d }
        });
        var processor = new FakeSpeechAudioProcessingService();
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);
        var handler = new SpeechAudioPreparationStageHandler(
            analyzer,
            new SpeechAudioPreparationPlanner(),
            processor,
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            mediaRepository,
            new FakeProjectStageRunStore());

        var result = await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(mediaAsset.ProjectId, mediaAsset, normalized, VocalStemArtifact: null, [normalized]),
            TestContext.Current.CancellationToken);

        Assert.Equal(normalized.RelativePath, result.AsrAudioArtifact.RelativePath);
        Assert.Single(processor.Requests);
        Assert.NotNull(result.AsrDecision.FallbackReason);
    }

    [Fact]
    public async Task StageHandler_does_not_save_processed_artifacts_when_later_processing_fails()
    {
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath);
        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(CreateAnalysis(SpeechAudioSourceKind.FullMix, [AudioQualityDefectKind.LowVolume]));
        analyzer.QueueResult(CreateAnalysis(SpeechAudioSourceKind.FullMix, []));
        var processor = new FakeSpeechAudioProcessingService { ThrowOnCallNumber = 2 };
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);
        var handler = new SpeechAudioPreparationStageHandler(
            analyzer,
            new SpeechAudioPreparationPlanner(),
            processor,
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            mediaRepository,
            new FakeProjectStageRunStore());

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new SpeechAudioPreparationStageRequest(mediaAsset.ProjectId, mediaAsset, normalized, VocalStemArtifact: null, [normalized]),
                TestContext.Current.CancellationToken));

        Assert.DoesNotContain(mediaRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.SpeechProcessedAudio);
    }

    [Fact]
    public async Task StageHandler_persists_project_relative_analysis_paths()
    {
        MediaAsset mediaAsset = CreateMediaAsset();
        ProjectArtifact normalized = CreateArtifact(mediaAsset, ArtifactKind.NormalizedAudio, ProjectArtifactPaths.NormalizedAudioRelativePath);
        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(CreateAnalysis(SpeechAudioSourceKind.FullMix, []) with
        {
            AudioPath = @"C:\Users\local\project\artifacts\audio\normalized.wav"
        });
        var mediaRepository = new FakeMediaAssetRepository();
        mediaRepository.Seed(mediaAsset);
        var artifactStore = new FakeArtifactStore();
        var handler = new SpeechAudioPreparationStageHandler(
            analyzer,
            new SpeechAudioPreparationPlanner(),
            new FakeSpeechAudioProcessingService(),
            artifactStore,
            new FakeFileFingerprintService(),
            mediaRepository,
            new FakeProjectStageRunStore());

        await handler.HandleAsync(
            new SpeechAudioPreparationStageRequest(mediaAsset.ProjectId, mediaAsset, normalized, VocalStemArtifact: null, [normalized]),
            TestContext.Current.CancellationToken);

        ProjectArtifact analysisArtifact = Assert.Single(mediaRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.AudioQualityAnalysis);
        SpeechAudioPreparationAudit? audit = await artifactStore.ReadJsonAsync<SpeechAudioPreparationAudit>(
            analysisArtifact.RelativePath,
            TestContext.Current.CancellationToken);

        Assert.NotNull(audit);
        Assert.Equal(normalized.RelativePath, audit!.FullMix.Analysis.AudioPath);
    }

    private static AudioQualityAnalysisResult CreateAnalysis(
        SpeechAudioSourceKind sourceKind,
        IReadOnlyList<AudioQualityDefectKind> defects) =>
        new(
            "virtual.wav",
            CreateMetrics(sourceKind),
            AudioQualityAnalysisThresholds.ForSource(sourceKind),
            defects,
            []);

    private static AudioQualityMetrics CreateMetrics(SpeechAudioSourceKind sourceKind) =>
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
            HissRatioDb: -10.0d,
            SpeechBandRatioDb: -3.0d,
            CrestFactorDb: 18.0d,
            DynamicRangeDb: 12.0d,
            NoiseFloorDbfs: -50.0d,
            SnrDb: 30.0d,
            AudioSnrConfidence.Reliable);

    private static MediaAsset CreateMediaAsset()
    {
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            12.0d,
            HasAudio: true,
            HasVideo: true,
            now);
    }

    private static ProjectArtifact CreateArtifact(MediaAsset mediaAsset, ArtifactKind kind, string relativePath) =>
        new(
            Guid.NewGuid(),
            mediaAsset.ProjectId,
            mediaAsset.Id,
            kind,
            relativePath,
            "hash",
            100,
            mediaAsset.DurationSeconds,
            48000,
            1,
            DateTimeOffset.UtcNow);
}
