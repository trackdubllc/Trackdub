using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class TextRefinementStageHandlerTests
{
    [Fact]
    public async Task HandleAsync_uses_text_refinement_asr_stage_name_for_asr_scope()
    {
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new TextRefinementStageHandler(new FakeTextRefinementEngine(), stageRunStore);
        Guid projectId = Guid.NewGuid();

        TextRefinementStageResult result = await handler.HandleAsync(
            new TextRefinementStageRequest(
                projectId,
                [new TextRefinementInputSegment(0, 0.0d, 1.0d, "hello world")],
                TextRefinementScope.Asr),
            TestContext.Current.CancellationToken);

        Assert.Equal(StageNames.TextRefinementAsr, result.StageRun.StageName);
        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Single(result.Segments);
        Assert.True(result.Segments[0].Accepted);
    }
}
