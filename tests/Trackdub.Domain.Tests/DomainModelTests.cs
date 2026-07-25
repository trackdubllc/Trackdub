using Trackdub.Domain;

namespace Trackdub.Domain.Tests;

public sealed class DomainModelTests
{
    [Fact]
    public void CreateNewProject_NormalizesNameAndPath()
    {
        ProjectRecord project = ProjectRecord.CreateNew(" Demo Project ", @".\workspace", DateTimeOffset.Parse("2026-04-19T12:00:00+00:00"));

        Assert.Equal("Demo Project", project.Name);
        Assert.True(Path.IsPathRooted(project.RootPath));
        Assert.Equal(project.CreatedAtUtc, project.UpdatedAtUtc);
    }

    [Theory]
    [InlineData(@"D:\absolute\artifact.json")]
    [InlineData(@"\\server\share\artifact.json")]
    public void RegisterArtifact_RejectsAbsolutePath(string path)
    {
        Assert.Throws<ArgumentException>(() => ArtifactRecord.Register(
            Guid.NewGuid(),
            null,
            "transcript",
            path,
            "abc123",
            "asr-stage",
            DateTimeOffset.UtcNow));
    }

    [Fact]
    public void CompleteStageRun_RejectsEarlierCompletionTime()
    {
        StageRunRecord stageRun = StageRunRecord.Start(Guid.NewGuid(), "asr", DateTimeOffset.Parse("2026-04-19T12:00:00+00:00"));

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            stageRun.Complete(DateTimeOffset.Parse("2026-04-19T11:59:59+00:00")));
    }

    [Fact]
    public void CancelStageRun_recordsTerminalCanceledStatus()
    {
        StageRunRecord stageRun = StageRunRecord.Start(
            Guid.NewGuid(),
            "asr",
            DateTimeOffset.Parse("2026-04-19T12:00:00+00:00"));

        StageRunRecord canceled = stageRun.Cancel(
            DateTimeOffset.Parse("2026-04-19T12:00:01+00:00"),
            "User canceled transcript generation.");

        Assert.Equal(StageRunStatus.Canceled, canceled.Status);
        Assert.Equal("User canceled transcript generation.", canceled.FailureReason);
        Assert.NotNull(canceled.CompletedAtUtc);
    }

    [Fact]
    public void SkipStageRun_recordsTerminalSkippedStatus()
    {
        StageRunRecord stageRun = StageRunRecord.Start(
            Guid.NewGuid(),
            "diarization",
            DateTimeOffset.Parse("2026-04-19T12:00:00+00:00"));

        StageRunRecord skipped = stageRun.Skip(
            DateTimeOffset.Parse("2026-04-19T12:00:01+00:00"),
            "Speaker diarization is disabled.");

        Assert.Equal(StageRunStatus.Skipped, skipped.Status);
        Assert.Equal("Speaker diarization is disabled.", skipped.FailureReason);
        Assert.NotNull(skipped.CompletedAtUtc);
    }

    [Fact]
    public void PartiallyCompleteStageRun_recordsTerminalPartialStatus()
    {
        StageRunRecord stageRun = StageRunRecord.Start(
            Guid.NewGuid(),
            "tts",
            DateTimeOffset.Parse("2026-04-19T12:00:00+00:00"));

        StageRunRecord partiallyCompleted = stageRun.PartiallyComplete(
            DateTimeOffset.Parse("2026-04-19T12:00:01+00:00"),
            "Generated 2 of 3 requested takes.");

        Assert.Equal(StageRunStatus.PartiallyCompleted, partiallyCompleted.Status);
        Assert.Equal("Generated 2 of 3 requested takes.", partiallyCompleted.FailureReason);
        Assert.NotNull(partiallyCompleted.CompletedAtUtc);
    }
}
