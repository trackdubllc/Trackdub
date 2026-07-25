using InfraOliveOptimizationProgress = Trackdub.Infrastructure.ModelOptimization.OliveOptimizationProgress;

namespace Trackdub.Application.Tests.ModelOptimization;

public sealed class OliveOptimizationProgressTests
{
    [Theory]
    [InlineData("Step 2/5", 2, 5)]
    [InlineData("  step 1 / 4  ", 1, 4)]
    public void TryParseStep_parses_olive_step_lines(string line, int expectedCurrent, int expectedTotal)
    {
        Assert.True(InfraOliveOptimizationProgress.TryParseStep(line, out int current, out int total));
        Assert.Equal(expectedCurrent, current);
        Assert.Equal(expectedTotal, total);
    }

    [Fact]
    public void TryFormatProgressLine_emits_structured_prefix()
    {
        Assert.True(InfraOliveOptimizationProgress.TryFormatProgressLine("Step 3/10", out string progressLine));
        Assert.Equal("[progress] Step 3/10", progressLine);
    }
}
