using Trackdub.Cli.Handlers;
using Trackdub.Contracts.Dubbing;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

public sealed class RunPipelineHandlerGoalTests
{
    private static readonly DateTimeOffset s_now = DateTimeOffset.UtcNow;

    [Fact]
    public void IsGoalAchieved_PartialSuccess_WithExportAndAllStagesOk_ReturnsTrue()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Succeeded),
            Outcome("Export", StageStatus.Succeeded, ["output/dubbed.mp4"]),
        ]);

        bool achieved = RunPipelineHandler.IsGoalAchieved(result, stageFilter: null, "output/dubbed.mp4");

        Assert.True(achieved);
    }

    [Fact]
    public void IsGoalAchieved_PartialSuccess_NoExportArtifact_ReturnsFalse()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Succeeded),
            Outcome("Export", StageStatus.Failed),
        ]);

        bool achieved = RunPipelineHandler.IsGoalAchieved(result, stageFilter: null, exportedFilePath: null);

        Assert.False(achieved);
    }

    [Fact]
    public void IsGoalAchieved_PartialSuccess_FailedRequestedStage_ReturnsFalse()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Failed, reasonCode: "STAGE_FAILED"),
            Outcome("Export", StageStatus.Succeeded, ["output/dubbed.mp4"]),
        ]);

        bool achieved = RunPipelineHandler.IsGoalAchieved(result, stageFilter: null, "output/dubbed.mp4");

        Assert.False(achieved);
    }

    [Fact]
    public void IsGoalAchieved_PartialSuccess_BenignSkip_ReturnsTrue()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Succeeded),
            Outcome("Separation", StageStatus.Skipped, reasonCode: StageSkipReasonCodes.NoSpeechRegions),
            Outcome("Export", StageStatus.Succeeded, ["output/dubbed.mp4"]),
        ]);

        bool achieved = RunPipelineHandler.IsGoalAchieved(result, stageFilter: null, "output/dubbed.mp4");

        Assert.True(achieved);
    }

    [Fact]
    public void IsGoalAchieved_StageFilter_OnlyChecksFilteredStages()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Succeeded),
            Outcome("TTS", StageStatus.Failed, reasonCode: "STAGE_FAILED"),
            Outcome("Export", StageStatus.Succeeded, ["output/dubbed.mp4"]),
        ]);

        IReadOnlyList<string> filter = ["ASR", "Export"];
        bool achieved = RunPipelineHandler.IsGoalAchieved(result, filter, "output/dubbed.mp4");

        Assert.True(achieved);
    }

    [Fact]
    public void IsGoalAchieved_StageFilterIncludesExport_NoExportArtifact_ReturnsFalse()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Succeeded),
            Outcome("Export", StageStatus.Failed),
        ]);

        IReadOnlyList<string> filter = ["ASR", "Export"];
        bool achieved = RunPipelineHandler.IsGoalAchieved(result, filter, exportedFilePath: null);

        Assert.False(achieved);
    }

    [Fact]
    public void IsGoalAchieved_StageFilterExcludesExport_NoExportNeeded()
    {
        var result = CreateResult(DubbingRunStatus.PartialSuccess,
        [
            Outcome("ASR", StageStatus.Succeeded),
            Outcome("Export", StageStatus.Failed),
        ]);

        IReadOnlyList<string> filter = ["ASR"];
        bool achieved = RunPipelineHandler.IsGoalAchieved(result, filter, exportedFilePath: null);

        Assert.True(achieved);
    }

    private static DubbingRunResult CreateResult(DubbingRunStatus status, IReadOnlyList<StageOutcome> outcomes)
        => new()
        {
            RunId = Guid.NewGuid(),
            StartTime = s_now.AddMinutes(-5),
            EndTime = s_now,
            OverallStatus = status,
            StageOutcomes = outcomes,
        };

    private static StageOutcome Outcome(string stageName, StageStatus status, IReadOnlyList<string>? artifacts = null, string? reasonCode = null)
        => new()
        {
            StageName = stageName,
            Status = status,
            StartTime = s_now.AddMinutes(-4),
            EndTime = s_now.AddMinutes(-1),
            ArtifactPaths = artifacts ?? [],
            ReasonCode = reasonCode,
        };
}
