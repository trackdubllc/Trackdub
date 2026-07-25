using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class AsrDeviceDegradationTests
{
    [Fact]
    public async Task AsrStageHandler_SurfacesDeviceDegradation_FromEngineReporter()
    {
        var degradation = new DeviceDegradationReport(
            DeviceDegradationKind.MemoryExhausted,
            0,
            "device-0",
            "out of memory",
            FallbackDeviceIndex: 1,
            FallbackAdapterDescription: "device-1");
        var engine = new FakeDeviceDegradationReportingEngine { DeviceDegradation = degradation };
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new AsrStageHandler(engine, stageRunStore);

        AsrStageResult result = await handler.HandleAsync(
            new AsrStageRequest(
                Guid.NewGuid(),
                "test.wav",
                [new SpeechRegion(0, 0.0, 1.0)]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(result.DeviceDegradation);
        Assert.Equal(DeviceDegradationKind.MemoryExhausted, result.DeviceDegradation!.Kind);
        Assert.Equal(0, result.DeviceDegradation.FailedDeviceIndex);
    }

    [Theory]
    [InlineData(DeviceDegradationKind.MemoryExhausted, "DEVICE_OOM")]
    [InlineData(DeviceDegradationKind.DeviceFailed, "DEVICE_FAILURE")]
    public async Task AsrGenerationStage_WritesDegradationRecord_WithExpectedCode(
        DeviceDegradationKind kind,
        string expectedCode)
    {
        var degradation = new DeviceDegradationReport(kind, 0, "device-0", "device error detail");
        var engine = new FakeDeviceDegradationReportingEngine { DeviceDegradation = degradation };
        var stageRunStore = new FakeProjectStageRunStore();
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var fingerprintService = new FakeFileFingerprintService(new FileFingerprint("hash", 1, DateTimeOffset.UtcNow));
        var degradationWriter = new PipelineDegradationWriter(artifactStore, fingerprintService, mediaAssetRepository);
        var handler = new AsrStageHandler(engine, stageRunStore);
        var stage = new AsrGenerationStage(handler, artifactStore, stageRunStore, degradationWriter);

        TranscriptGenerationContext context = CreateContext();

        await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        ProjectArtifact artifact = Assert.Single(mediaAssetRepository.Artifacts);
        Assert.Equal(expectedCode, artifact.DegradationCode);
        Assert.Equal(StageNames.Asr, artifact.DegradationStage);
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
            sourceLanguage: "en")
        {
            RegionPlan = new TranscriptRegionPlan(
                [new SpeechRegion(0, 0.0, 1.0)],
                new Dictionary<int, Guid>())
        };
    }

    private sealed class FakeDeviceDegradationReportingEngine : IAudioTranscriptionEngine, IDeviceDegradationReporter
    {
        public DeviceDegradationReport? DeviceDegradation { get; set; }

        public DeviceDegradationReport? LastDeviceDegradation => DeviceDegradation;

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(
            [
                new RecognizedTranscriptSegment(
                    regions[0].Index,
                    regions[0].StartSeconds,
                    regions[0].EndSeconds,
                    "hello",
                    "en")
            ]);
    }
}
