using Trackdub.Domain;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Transcript;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteTranscriptRepositoryTests
{
    [Fact]
    public async Task Repository_round_trips_current_revision_segments_and_stage_runs()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Transcript.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Transcript", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            StageRunRecord asrStageRun = StageRunRecord.Start(project.Id, "asr", now)
                .WithRuntimeInfo("auto", "cpu", "onnx-community/whisper-tiny", "whisper-tiny-onnx", "int8", "bootstrap skipped")
                .Complete(now.AddSeconds(2));
            await stageRunStore.CreateAsync(asrStageRun, TestContext.Current.CancellationToken);
            await stageRunStore.UpdateAsync(asrStageRun, TestContext.Current.CancellationToken);

            TranscriptRevision revision = TranscriptRevision.Create(project.Id, asrStageRun.Id, revisionNumber: 1, now.AddSeconds(3));
            TranscriptSegment[] segments =
            [
                TranscriptSegment.Create(
                    revision.Id,
                    0,
                    0.0,
                    1.5,
                    "Hello",
                    detectedLanguage: "en",
                    words:
                    [
                        TranscriptWord.Create(0, 0.0, 0.7, "Hel", 0.62d),
                        TranscriptWord.Create(1, 0.7, 1.5, "lo", 0.91d)
                    ]),
                TranscriptSegment.Create(revision.Id, 1, 1.5, 3.0, "World", detectedLanguage: "en")
            ];

            await transcriptRepository.SaveRevisionAsync(revision, segments, TestContext.Current.CancellationToken);

            TranscriptRevision? current = await transcriptRepository.GetCurrentRevisionAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<TranscriptSegment> reloadedSegments = await transcriptRepository.GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<StageRunRecord> stageRuns = await stageRunStore.ListByProjectAsync(project.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(current);
            Assert.Equal(asrStageRun.Id, current!.StageRunId);
            Assert.Equal(2, reloadedSegments.Count);
            Assert.Equal("Hello", reloadedSegments[0].Text);
            Assert.Equal("en", reloadedSegments[0].DetectedLanguage);
            Assert.Equal(2, reloadedSegments[0].Words.Count);
            Assert.Equal("Hel", reloadedSegments[0].Words[0].Text);
            Assert.Equal(0.62d, reloadedSegments[0].Words[0].Confidence);
            Assert.Empty(reloadedSegments[1].Words);
            Assert.Single(stageRuns);
            Assert.Equal(StageRunStatus.Completed, stageRuns[0].Status);
            Assert.NotNull(stageRuns[0].RuntimeInfo);
            Assert.Equal("cpu", stageRuns[0].RuntimeInfo!.SelectedProvider);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
