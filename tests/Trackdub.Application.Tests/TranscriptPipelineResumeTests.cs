using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Application.Projects;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TranscriptPipelineResumeTests
{
    [Fact]
    public async Task ExecuteAsync_skips_resumable_vad_stage_and_hydrates_speech_regions()
    {
        var artifactStore = new FakeArtifactStore(Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N")));
        var mediaRepository = new FakeMediaAssetRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var transcriptRepository = new FakeTranscriptRepository();
        var artifactWriter = new TranscriptArtifactWriter(
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("hash", 1, DateTimeOffset.UtcNow)),
            mediaRepository);

        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Resume", now, now);
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            10.0d,
            HasAudio: true,
            HasVideo: false,
            now);
        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            "artifacts/audio.wav",
            "audio-hash",
            100,
            10.0d,
            16000,
            1,
            now);

        StageRunRecord vadRun = StageRunRecord.Start(projectId, StageNames.Vad, now).Complete(now);
        mediaRepository.Seed(mediaAsset);
        await mediaRepository.SaveArtifactAsync(audioArtifact, CancellationToken.None);
        await artifactWriter.WriteSpeechRegionsArtifactAsync(
            projectId,
            mediaAsset,
            [new SpeechRegion(0, 0.0, 2.0)],
            vadRun.Id,
            CancellationToken.None);

        var vadDetector = new FakeSpeechRegionDetector();
        var vadStage = new VadGenerationStage(
            new VadStageHandler(vadDetector, stageRunStore),
            artifactWriter,
            artifactStore);
        var pipeline = new TranscriptGenerationPipeline(
            [new NoOpEnhancementStage(), vadStage],
            artifactStore,
            artifactWriter,
            transcriptRepository,
            stageRunStore: stageRunStore);

        var resumeState = new TranscriptProjectState(
            new OpenProjectResult(
                project,
                mediaAsset,
                null,
                SourceMediaStatus.Available,
                null,
                mediaRepository.Artifacts,
                null),
            CurrentTranscriptRevision: null,
            TranscriptSegments: [],
            Speakers: [],
            SpeakerTurns: [],
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: null,
            StageRuns: [vadRun],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);

        var context = new TranscriptGenerationContext(
            project,
            mediaAsset,
            audioArtifact,
            TranscriptAudioRoutingPlan.Raw(audioArtifact, SpeechAudioSourceKind.FullMix),
            enableSpeakerDiarization: false,
            sourceLanguage: "en")
        {
            ExecutionSnapshot = new Dictionary<string, string>(),
            ProjectState = resumeState,
            ProjectRootPath = artifactStore.GetPath("."),
            ForceRerun = false
        };

        TranscriptGenerationContext result = await pipeline.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(0, vadDetector.DetectCallCount);
        Assert.Single(result.SpeechRegions);
        StageRunRecord skippedRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.Vad, skippedRun.StageName);
        Assert.Equal(StageRunStatus.Skipped, skippedRun.Status);
    }

    private sealed class NoOpEnhancementStage : ITranscriptGenerationStage
    {
        public string StageName => StageNames.SpeechEnhancement;

        public Task<TranscriptGenerationContext> ExecuteAsync(
            TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<PipelineProgressEvent>? progress = null) =>
            Task.FromResult(context);
    }
}
