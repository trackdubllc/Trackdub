using Trackdub.Cli;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

public sealed class CliStageFilterTests
{
    [Fact]
    public void Model_override_accepts_explicit_lip_synthesis_alias()
    {
        Dictionary<string, string>? models = CliModelOverrides.Parse(
            ["lip-synthesis:latentsync-1.6"]);

        Assert.NotNull(models);
        Assert.Equal("latentsync-1.6", models[StageNames.LipSynthesis]);
    }

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

    [Fact]
    public void Build_RepairFlag_ProducesMixBeforeRepair()
    {
        IReadOnlyList<string>? stages = CliStageFilter.Build(null, null, repairLips: true);

        Assert.NotNull(stages);
        Assert.Equal([StageNames.Tts, StageNames.Export, StageNames.LipSynthesis],
            stages.TakeLast(3));
        Assert.DoesNotContain(StageNames.LipSync, stages);
    }

    [Fact]
    public void Build_BothFlags_InsertsLipSyncBeforeMixAndRepair()
    {
        IReadOnlyList<string>? stages = CliStageFilter.Build(null, null,
            lipSync: true, repairLips: true);

        Assert.NotNull(stages);
        Assert.Equal([StageNames.Tts, StageNames.LipSync, StageNames.Export, StageNames.LipSynthesis],
            stages.TakeLast(4));
    }

    [Fact]
    public void Build_FlagAndOnly_IsRejected()
    {
        Assert.Empty(CliStageFilter.Build(null, [StageNames.Asr], lipSync: true)!);
    }

    [Fact]
    public void Build_ResumeAfterRequestedLipStage_IsRejected()
    {
        Assert.Empty(CliStageFilter.Build(StageNames.Export, null, lipSync: true)!);
    }

    [Fact]
    public void Build_ResumeFromTtsWithRepair_IncludesExportAndRepair()
    {
        IReadOnlyList<string>? stages = CliStageFilter.Build(StageNames.Tts, null,
            repairLips: true);

        Assert.Equal([StageNames.Tts, StageNames.Export, StageNames.LipSynthesis], stages);
    }
}
