using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class AsrGenerationStageTests
{
    [Fact]
    public async Task ExecuteAsync_writes_degradation_when_asr_runs_on_nonempty_regions_but_returns_no_segments()
    {
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var degradationWriter = new PipelineDegradationWriter(
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("hash", 1, DateTimeOffset.UtcNow)),
            mediaAssetRepository);
        var asrHandler = new AsrStageHandler(new EmptyResultTranscriptionEngine(), stageRunStore);
        var stage = new AsrGenerationStage(asrHandler, artifactStore, stageRunStore, degradationWriter);

        TranscriptGenerationContext context = CreateContext() with
        {
            RegionPlan = new TranscriptRegionPlan(
                [new SpeechRegion(0, 0.0, 2.0)],
                new Dictionary<int, Guid>())
        };

        TranscriptGenerationContext result = await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(result.AsrResult);
        Assert.Empty(result.AsrResult!.Segments);
        ProjectArtifact degradationArtifact = Assert.Single(
            mediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.PipelineDegradation);
        Assert.Equal("ASR_EMPTY_RESULT", degradationArtifact.DegradationCode);
        Assert.Equal(StageNames.Asr, degradationArtifact.DegradationStage);
    }

    private static TranscriptGenerationContext CreateContext()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Demo", now, now);
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            1.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var audioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            "artifacts/audio.wav",
            "audio-hash",
            100,
            1.0d,
            16000,
            1,
            now);

        return new TranscriptGenerationContext(
            project,
            mediaAsset,
            audioArtifact,
            TranscriptAudioRoutingPlan.Raw(audioArtifact, SpeechAudioSourceKind.FullMix),
            enableSpeakerDiarization: false,
            sourceLanguage: "en");
    }

    private sealed class EmptyResultTranscriptionEngine : IAudioTranscriptionEngine
    {
        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>([]);
    }
}
