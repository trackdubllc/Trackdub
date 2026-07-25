using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class AsrStageHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithValidInput_CompletesSuccessfully()
    {
        var engine = new FakeAsrStageHandlerEngine();
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new AsrStageHandler(engine, stageRunStore);
        Guid projectId = Guid.NewGuid();

        AsrStageResult result = await handler.HandleAsync(
            new AsrStageRequest(
                projectId,
                "test.wav",
                [new SpeechRegion(0, 0.0, 1.0)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Single(result.Segments);
        Assert.Equal(1, engine.TranscribeCallCount);
    }

    [Fact]
    public async Task HandleAsync_WhenCanceled_RecordsCanceledStatus()
    {
        var engine = new FakeAsrStageHandlerEngine();
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new AsrStageHandler(engine, stageRunStore);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new AsrStageRequest(
                    Guid.NewGuid(),
                    "test.wav",
                    [new SpeechRegion(0, 0.0, 1.0)]),
                cts.Token));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, run.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenEngineFails_RecordsFailedStatus()
    {
        var engine = new FakeAsrStageHandlerEngine
        {
            ExceptionToThrow = new InvalidOperationException("ASR failed")
        };
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new AsrStageHandler(engine, stageRunStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new AsrStageRequest(
                    Guid.NewGuid(),
                    "test.wav",
                    [new SpeechRegion(0, 0.0, 1.0)]),
                TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, run.Status);
    }

    private sealed class FakeAsrStageHandlerEngine : IAudioTranscriptionEngine
    {
        public int TranscribeCallCount { get; private set; }

        public Exception? ExceptionToThrow { get; init; }

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            string normalizedAudioPath,
            IReadOnlyList<SpeechRegion> regions,
            CancellationToken cancellationToken) =>
            TranscribeAsync(
                new AudioTranscriptionRequest(normalizedAudioPath, regions),
                cancellationToken);

        public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
            AudioTranscriptionRequest request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            TranscribeCallCount++;

            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            return Task.FromResult<IReadOnlyList<RecognizedTranscriptSegment>>(
            [
                new RecognizedTranscriptSegment(
                    request.Regions[0].Index,
                    request.Regions[0].StartSeconds,
                    request.Regions[0].EndSeconds,
                    "hello",
                    "en")
            ]);
        }
    }
}
