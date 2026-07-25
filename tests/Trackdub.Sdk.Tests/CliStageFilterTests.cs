using Trackdub.Cli;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

public sealed class CliStageFilterTests
{
    [Fact]
    public void Build_FromTts_ExcludesLipStages()
    {
        IReadOnlyList<string>? stages = CliStageFilter.Build(StageNames.Tts, onlyStages: null);

        Assert.NotNull(stages);
        Assert.Equal(
            [
                StageNames.Tts,
                StageNames.Export,
            ],
            stages);
        Assert.DoesNotContain(StageNames.LipSync, stages);
        Assert.DoesNotContain(StageNames.LipSynthesis, stages);
    }

    [Fact]
    public void Build_FromLipSync_IncludesExtendedTail()
    {
        IReadOnlyList<string>? stages = CliStageFilter.Build(StageNames.LipSync, onlyStages: null);

        Assert.NotNull(stages);
        Assert.Equal(
            [
                StageNames.LipSync,
                StageNames.Export,
                StageNames.LipSynthesis,
            ],
            stages);
    }

    [Fact]
    public void Build_OnlyLipSync_ReturnsRequestedStage()
    {
        IReadOnlyList<string>? stages = CliStageFilter.Build(fromStage: null, onlyStages: [StageNames.LipSync]);

        Assert.NotNull(stages);
        Assert.Equal([StageNames.LipSync], stages);
    }
}
