using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

public sealed class TrackdubDubbingEngineStageRunOutcomeTests
{
    [Theory]
    [InlineData(StageRunStatus.Completed, StageStatus.Succeeded, null)]
    [InlineData(StageRunStatus.Skipped, StageStatus.Skipped, "No TTS takes")]
    [InlineData(StageRunStatus.Failed, StageStatus.Failed, "Aligner unavailable")]
    [InlineData(StageRunStatus.Canceled, StageStatus.Failed, "Cancelled by user")]
    public void MapStageRunToSdkOutcome_MapsTerminalStatuses(
        StageRunStatus stageRunStatus,
        StageStatus expectedStatus,
        string? failureReason)
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var stageRun = new StageRunRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            StageNames.LipSync,
            stageRunStatus,
            started,
            started.AddSeconds(1),
            failureReason);

        (StageStatus status, string? reasonCode, IReadOnlyList<string>? degradations) =
            TrackdubDubbingEngine.MapStageRunToSdkOutcome(stageRun);

        Assert.Equal(expectedStatus, status);
        if (failureReason is null)
        {
            Assert.Null(reasonCode);
        }
        else
        {
            Assert.Equal(failureReason, reasonCode);
        }

        Assert.Null(degradations);
    }

    [Fact]
    public void MapStageRunToSdkOutcome_PartiallyCompleted_ReturnsSucceededWithDegradation()
    {
        DateTimeOffset started = DateTimeOffset.UtcNow;
        var stageRun = new StageRunRecord(
            Guid.NewGuid(),
            Guid.NewGuid(),
            StageNames.LipSync,
            StageRunStatus.PartiallyCompleted,
            started,
            started.AddSeconds(1),
            "2 partial, 1 failed, 3 aligned.");

        (StageStatus status, string? reasonCode, IReadOnlyList<string>? degradations) =
            TrackdubDubbingEngine.MapStageRunToSdkOutcome(stageRun);

        Assert.Equal(StageStatus.Succeeded, status);
        Assert.Null(reasonCode);
        Assert.Equal(["2 partial, 1 failed, 3 aligned."], degradations);
    }

    [Fact]
    public void MapStageRunToSdkOutcome_MissingStageRun_ReturnsFailed()
    {
        (StageStatus status, string? reasonCode, _) =
            TrackdubDubbingEngine.MapStageRunToSdkOutcome(null);

        Assert.Equal(StageStatus.Failed, status);
        Assert.Equal("STAGE_RUN_MISSING", reasonCode);
    }

    [Fact]
    public void DetermineOverallStatus_AllRuntimeSkips_ReturnsFailed()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>
        {
            new()
            {
                StageName = StageNames.LipSync,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = "No TTS takes provided; lip-sync prerequisite not met.",
            },
        };

        Assert.Equal(DubbingRunStatus.Failed, TrackdubDubbingEngine.DetermineOverallStatus(outcomes));
    }

    [Fact]
    public void DetermineOverallStatus_AllBenignResumeSkips_ReturnsSucceeded()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>
        {
            new()
            {
                StageName = StageNames.Asr,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = StageSkipReasonCodes.ExistingArtifactsValid,
            },
            new()
            {
                StageName = StageNames.Translation,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = StageSkipReasonCodes.ExistingArtifactsValid,
            },
        };

        Assert.Equal(DubbingRunStatus.Succeeded, TrackdubDubbingEngine.DetermineOverallStatus(outcomes));
    }

    [Fact]
    public void DetermineOverallStatus_BenignSkipWithLaterSuccess_ReturnsSucceeded()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>
        {
            new()
            {
                StageName = StageNames.Tts,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = StageSkipReasonCodes.ExistingArtifactsValid,
            },
            new()
            {
                StageName = StageNames.Export,
                Status = StageStatus.Succeeded,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = ["export.mp4"],
            },
        };

        Assert.Equal(DubbingRunStatus.Succeeded, TrackdubDubbingEngine.DetermineOverallStatus(outcomes));
    }

    [Fact]
    public void DetermineOverallStatus_NoTranscriptSkipsWithExport_ReturnsSucceeded()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>
        {
            new()
            {
                StageName = StageNames.Translation,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = StageSkipReasonCodes.NoTranscriptSegments,
            },
            new()
            {
                StageName = StageNames.Tts,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = StageSkipReasonCodes.NoTranscriptSegments,
            },
            new()
            {
                StageName = StageNames.Export,
                Status = StageStatus.Succeeded,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = ["exports/dubbed.mp4"],
            },
        };

        Assert.Equal(DubbingRunStatus.Succeeded, TrackdubDubbingEngine.DetermineOverallStatus(outcomes));
    }

    [Fact]
    public void ShouldRunPostLipSynthesisExport_WhenBothStagesRanAndLipSucceeded_ReturnsTrue()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>
        {
            new()
            {
                StageName = StageNames.Export,
                Status = StageStatus.Succeeded,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = ["export.mp4"],
            },
            new()
            {
                StageName = StageNames.LipSynthesis,
                Status = StageStatus.Succeeded,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
            },
        };

        string[] stages = [StageNames.Export, StageNames.LipSynthesis];

        Assert.True(TrackdubDubbingEngine.ShouldRunPostLipSynthesisExport(stages, outcomes));
    }

    [Fact]
    public void ShouldRunPostLipSynthesisExport_WhenLipSkipped_ReturnsFalse()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var outcomes = new List<StageOutcome>
        {
            new()
            {
                StageName = StageNames.LipSynthesis,
                Status = StageStatus.Skipped,
                StartTime = now,
                EndTime = now,
                ArtifactPaths = [],
                ReasonCode = "LipSynthesisLicenseGate",
            },
        };

        string[] stages = [StageNames.Export, StageNames.LipSynthesis];

        Assert.False(TrackdubDubbingEngine.ShouldRunPostLipSynthesisExport(stages, outcomes));
    }
}
