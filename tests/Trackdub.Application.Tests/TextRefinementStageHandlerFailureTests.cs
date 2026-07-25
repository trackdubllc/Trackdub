using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TextRefinementStageHandlerFailureTests
{
    [Fact]
    public async Task HandleAsync_WhenEngineFails_RecordsFailedStatus()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new TextRefinementStageHandler(new ThrowingTextRefinementEngine(), stageRunStore);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            handler.HandleAsync(
                new TextRefinementStageRequest(
                    Guid.NewGuid(),
                    [new TextRefinementInputSegment(0, 0.0d, 1.0d, "hello")],
                    TextRefinementScope.Asr),
                TestContext.Current.CancellationToken));

        StageRunRecord run = Assert.Single(stageRunStore.All);
        Assert.Equal(StageNames.TextRefinementAsr, run.StageName);
        Assert.Equal(StageRunStatus.Failed, run.Status);
    }

    private sealed class ThrowingTextRefinementEngine : ITextRefinementEngine
    {
        public string EngineFamily => "throwing";

        public Task<IReadOnlyList<RefinedTextSegment>> RefineAsync(
            TextRefinementRequest request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Refinement failed");
    }
}
