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

public sealed class TextRefinementGenerationStageTests
{
    [Fact]
    public async Task ExecuteAsync_skips_when_toggle_disabled()
    {
        TextRefinementGenerationStage stage = CreateStage();
        TranscriptGenerationContext context = CreateContext(enableAsrTextRefinement: false);

        TranscriptGenerationContext result = await stage.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Same(context.AsrResult, result.AsrResult);
        Assert.NotNull(result.TextRefinementResult);
        Assert.Equal(StageRunStatus.Skipped, result.TextRefinementResult.StageRun.Status);
    }

    [Fact]
    public async Task ExecuteAsync_preserves_asr_result_when_fake_engine_polishes()
    {
        TextRefinementGenerationStage stage = CreateStage();
        TranscriptGenerationContext context = CreateContext(enableAsrTextRefinement: true);
        string originalSegmentText = context.AsrResult!.Segments[0].Text;

        TranscriptGenerationContext result = await stage.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Same(context.AsrResult, result.AsrResult);
        Assert.Equal(originalSegmentText, result.AsrResult!.Segments[0].Text);
        Assert.NotNull(result.TextRefinementResult);
        Assert.Equal(StageRunStatus.Completed, result.TextRefinementResult.StageRun.Status);
        Assert.True(result.TextRefinementResult.Segments[0].Accepted);
        Assert.Equal("Hello world.", result.TextRefinementResult.Segments[0].DisplayedText);
    }

    [Fact]
    public async Task ExecuteAsync_skips_with_degradation_when_engine_unavailable()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var stage = new TextRefinementGenerationStage(
            new TextRefinementStageHandler(new ThrowingTextRefinementEngine(), stageRunStore),
            stageRunStore);
        TranscriptGenerationContext context = CreateContext(enableAsrTextRefinement: true);

        TranscriptGenerationContext result = await stage.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Same(context.AsrResult, result.AsrResult);
        Assert.NotNull(result.TextRefinementResult);
        Assert.Equal(StageRunStatus.Skipped, result.TextRefinementResult.StageRun.Status);
    }

    [Fact]
    public async Task ExecuteAsync_marks_fallback_when_fake_engine_returns_unchanged_polish()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var stage = new TextRefinementGenerationStage(
            new TextRefinementStageHandler(
                new FakeTextRefinementEngine(
                    textFactory: static (_, segment) => segment.Text,
                    acceptFactory: static (_, _) => true),
                stageRunStore),
            stageRunStore);
        TranscriptGenerationContext context = CreateContext(enableAsrTextRefinement: true);

        TranscriptGenerationContext result = await stage.ExecuteAsync(
            context,
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.TextRefinementResult!.StageRun.Status);
        Assert.False(result.TextRefinementResult.Segments[0].Accepted);
        Assert.Equal("Hello world", result.TextRefinementResult.Segments[0].DisplayedText);
    }

    private static TextRefinementGenerationStage CreateStage()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        return new TextRefinementGenerationStage(
            new TextRefinementStageHandler(new FakeTextRefinementEngine(), stageRunStore),
            stageRunStore);
    }

    private static TranscriptGenerationContext CreateContext(bool enableAsrTextRefinement)
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
        var asrStageRun = StageRunRecord.Start(projectId, StageNames.Asr, now);
        var asrResult = new AsrStageResult(
            asrStageRun,
            [
                new RecognizedTranscriptSegment(
                    0,
                    0.0d,
                    1.0d,
                    "Hello world")
            ]);

        return new TranscriptGenerationContext(
            project,
            mediaAsset,
            audioArtifact,
            TranscriptAudioRoutingPlan.Raw(audioArtifact, SpeechAudioSourceKind.FullMix),
            enableSpeakerDiarization: false,
            sourceLanguage: "en",
            modelPreferences: new InferenceModelPreferences(EnableAsrTextRefinement: enableAsrTextRefinement))
        {
            AsrResult = asrResult
        };
    }

    private sealed class ThrowingTextRefinementEngine : ITextRefinementEngine
    {
        public string EngineFamily => "throwing-text-refinement";

        public Task<IReadOnlyList<RefinedTextSegment>> RefineAsync(
            TextRefinementRequest request,
            CancellationToken cancellationToken) =>
            throw new FileNotFoundException("genai_config.json is missing.");
    }
}
