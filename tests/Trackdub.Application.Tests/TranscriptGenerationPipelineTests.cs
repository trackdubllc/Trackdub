using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TranscriptGenerationPipelineTests
{
    [Fact]
    public async Task ExecuteAsync_WhenStageThrows_WritesPipelineDegradationAndRethrows()
    {
        var artifactStore = new FakeArtifactStore();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var degradationWriter = new PipelineDegradationWriter(
            artifactStore,
            new FakeFileFingerprintService(new FileFingerprint("degradation-hash", 64, DateTimeOffset.UtcNow)),
            mediaAssetRepository);
        var pipeline = new TranscriptGenerationPipeline(
            [new ThrowingTranscriptGenerationStage(StageNames.Asr)],
            degradationWriter: degradationWriter);
        TranscriptGenerationContext context = CreateContext();

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal("stage failed", exception.Message);
        ProjectArtifact degradation = Assert.Single(mediaAssetRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.PipelineDegradation);
        Assert.Equal(StageNames.Asr, degradation.DegradationStage);
        Assert.Equal("PIPELINE_STAGE_UNHANDLED_EXCEPTION", degradation.DegradationCode);
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

    [Fact]
    public async Task ExecuteAsync_StagesRunInRegistrationOrder()
    {
        var executionOrder = new List<string>();
        var pipeline = new TranscriptGenerationPipeline(
        [
            new RecordingStage(StageNames.Vad, executionOrder),
            new RecordingStage(StageNames.Diarization, executionOrder),
            new RecordingStage(StageNames.Asr, executionOrder),
        ]);
        TranscriptGenerationContext context = CreateContext();

        await pipeline.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.Equal(3, executionOrder.Count);
        Assert.Equal([StageNames.Vad, StageNames.Diarization, StageNames.Asr], executionOrder);
    }

    [Fact]
    public async Task ExecuteAsync_ReportsStartedAndTerminalEventsForRunnableStage()
    {
        var executionOrder = new List<string>();
        var events = new List<PipelineProgressEvent>();
        var pipeline = new TranscriptGenerationPipeline(
        [
            new RecordingStage(StageNames.Asr, executionOrder),
        ]);
        TranscriptGenerationContext context = CreateContext();

        await pipeline.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken,
            new RecordingProgress(events));

        Assert.Contains(events, e =>
            e.StageKey == StageNames.Asr &&
            e.EventKind == PipelineProgressEventKind.Started);
        PipelineProgressEvent completed = Assert.Single(events, e =>
            e.StageKey == StageNames.Asr &&
            e.EventKind == PipelineProgressEventKind.Completed);
        Assert.Equal(100d, completed.PercentComplete);
    }

    [Fact]
    public async Task ExecuteAsync_WhenStageThrows_SubsequentStagesAreSkipped()
    {
        var executionOrder = new List<string>();
        var pipeline = new TranscriptGenerationPipeline(
        [
            new RecordingStage(StageNames.Vad, executionOrder),
            new RecordingStage(StageNames.Diarization, executionOrder),
            new ThrowingTranscriptGenerationStage(StageNames.Asr),
        ]);
        TranscriptGenerationContext context = CreateContext();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            pipeline.ExecuteAsync(context, TestContext.Current.CancellationToken));

        Assert.Equal([StageNames.Vad, StageNames.Diarization], executionOrder);
    }

    [Fact]
    public async Task ExecuteAsync_StagesReceiveContextFromPreviousStage()
    {
        TranscriptGenerationContext context = CreateContext();

        var pipeline = new TranscriptGenerationPipeline(
        [
            new VadResultInjectorStage(),
            new DiarizationResultInjectorStage(),
        ]);

        TranscriptGenerationContext result = await pipeline.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(result.VadStageRunId);
        Assert.NotNull(result.DiarizationResult);
    }

    [Fact]
    public async Task ExecuteAsync_WithSingleStage_ReturnsStageOutput()
    {
        var pipeline = new TranscriptGenerationPipeline(
        [
            new VadResultInjectorStage(),
        ]);
        TranscriptGenerationContext context = CreateContext();

        TranscriptGenerationContext result = await pipeline.ExecuteAsync(context, TestContext.Current.CancellationToken);

        Assert.NotNull(result.VadStageRunId);
        Assert.NotEqual(context, result);
    }

    /// <summary>
    /// A stage that records its name in a shared list when executed, confirming execution order.
    /// </summary>
    private sealed class RecordingStage(string stageName, List<string> executionOrder) : ITranscriptGenerationStage
    {
        public string StageName { get; } = stageName;

        public Task<TranscriptGenerationContext> ExecuteAsync(
            TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<PipelineProgressEvent>? progress = null)
        {
            executionOrder.Add(StageName);
            return Task.FromResult(context);
        }
    }

    /// <summary>
    /// A stage that injects a VadStageRunId, simulating what VadGenerationStage would do.
    /// </summary>
    private sealed class VadResultInjectorStage : ITranscriptGenerationStage
    {
        public string StageName => StageNames.Vad;

        public Task<TranscriptGenerationContext> ExecuteAsync(
            TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<PipelineProgressEvent>? progress = null)
        {
            TranscriptGenerationContext result = context with { VadStageRunId = Guid.NewGuid() };
            return Task.FromResult(result);
        }
    }

    /// <summary>
    /// A stage that injects a DiarizationResult, simulating what SpeakerDiarizationStage would do.
    /// </summary>
    private sealed class DiarizationResultInjectorStage : ITranscriptGenerationStage
    {
        public string StageName => StageNames.Diarization;

        public Task<TranscriptGenerationContext> ExecuteAsync(
            TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<PipelineProgressEvent>? progress = null)
        {
            TranscriptGenerationContext result = context with
            {
                DiarizationResult = new DiarizationResult(
                    [],
                    []),
            };
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingTranscriptGenerationStage(string stageName) : ITranscriptGenerationStage
    {
        public string StageName { get; } = stageName;

        public Task<TranscriptGenerationContext> ExecuteAsync(
            TranscriptGenerationContext context,
            CancellationToken cancellationToken,
            IProgress<PipelineProgressEvent>? progress = null) =>
            throw new InvalidOperationException("stage failed");
    }

    private sealed class RecordingProgress(List<PipelineProgressEvent> events) : IProgress<PipelineProgressEvent>
    {
        public void Report(PipelineProgressEvent value) => events.Add(value);
    }
}
