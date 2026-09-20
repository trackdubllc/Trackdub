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
        ProjectArtifact[] degradationArtifacts = [.. mediaAssetRepository.Artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.PipelineDegradation)];
        Assert.Contains(
            degradationArtifacts,
            artifact => artifact.DegradationCode == "ASR_EMPTY_RESULT_FALLBACK");
        Assert.Contains(
            degradationArtifacts,
            artifact => artifact.DegradationCode == "ASR_EMPTY_RESULT");
        Assert.All(
            degradationArtifacts,
            artifact => Assert.Equal(StageNames.Asr, artifact.DegradationStage));
    }

    [Fact]
    public async Task ExecuteAsync_empty_primary_asr_retries_with_fallback_alias_and_uses_fallback_segments()
    {
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var degradationWriter = new PipelineDegradationWriter(
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("hash", 1, DateTimeOffset.UtcNow)),
            mediaAssetRepository);
        var transcriptionEngine = new FallbackOnlyTranscriptionEngine();
        var asrHandler = new AsrStageHandler(transcriptionEngine, stageRunStore);
        var stage = new AsrGenerationStage(asrHandler, artifactStore, stageRunStore, degradationWriter);

        TranscriptGenerationContext context = CreateContext(
            new InferenceModelPreferences(
                AsrModelAlias: "nemotron-3.5-asr",
                RequireAsrModelAlias: true)) with
        {
            RegionPlan = new TranscriptRegionPlan(
                [new SpeechRegion(0, 0.0, 2.0)],
                new Dictionary<int, Guid>())
        };

        TranscriptGenerationContext result = await stage.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(result.AsrResult);
        Assert.Single(result.AsrResult!.Segments);
        Assert.Equal(2, transcriptionEngine.CallCount);
        Assert.Equal("qwen3-asr-0.6b", transcriptionEngine.LastPreferredAlias);
        Assert.Contains(
            mediaAssetRepository.Artifacts,
            artifact => artifact.DegradationCode == "ASR_EMPTY_RESULT_FALLBACK");
    }

    private static TranscriptGenerationContext CreateContext(InferenceModelPreferences? modelPreferences = null)
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
            sourceLanguage: "en",
            modelPreferences);
    }

    private sealed class FallbackOnlyTranscriptionEngine : IAudioTranscriptionEngine
    {
        public int CallCount { get; private set; }

        public string? LastPreferredAlias { get; private set; }

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>([]);

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            LastPreferredAlias = request.Options?.PreferredModelAlias;
            CallCount++;
            if (CallCount == 1)
            {
                return Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>([]);
            }

            IReadOnlyList<RecognizedTranscriptSegment> segments =
            [
                new RecognizedTranscriptSegment(
                    0,
                    0.0,
                    2.0,
                    "fallback transcript",
                    DetectedLanguage: "en")
            ];
            return Task.FromResult(segments);
        }
    }

    private sealed class EmptyResultTranscriptionEngine : IAudioTranscriptionEngine
    {
        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>([]);

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>([]);
    }
}
