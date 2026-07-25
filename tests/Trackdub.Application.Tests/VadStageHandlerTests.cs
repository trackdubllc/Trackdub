using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class VadStageHandlerTests
{
    [Fact]
    public async Task HandleAsync_WithValidInput_CompletesSuccessfully()
    {
        var detector = new FakeSpeechRegionDetector();
        detector.SetRegions(new SpeechRegion(0, 1.0, 2.0));
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new VadStageHandler(detector, stageRunStore);

        VadStageResult result = await handler.HandleAsync(
            new VadStageRequest(Guid.NewGuid(), "test.wav", 10.0),
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Single(result.Regions);
    }

    [Fact]
    public async Task HandleAsync_WhenCanceled_RecordsCanceledStatus()
    {
        var detector = new FakeSpeechRegionDetector();
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new VadStageHandler(detector, stageRunStore);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new VadStageRequest(Guid.NewGuid(), "test.wav", 10.0),
                cts.Token));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, run.Status);
    }

    [Fact]
    public async Task HandleAsync_WhenDetectorFails_RecordsFailedStatus()
    {
        var detector = new FakeSpeechRegionDetector();
        detector.SetException(new InvalidOperationException("Detector failed"));
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new VadStageHandler(detector, stageRunStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new VadStageRequest(Guid.NewGuid(), "test.wav", 10.0),
                TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Failed, run.Status);
    }
}
