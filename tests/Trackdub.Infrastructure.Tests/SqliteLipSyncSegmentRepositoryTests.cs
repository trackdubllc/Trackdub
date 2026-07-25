using Microsoft.Data.Sqlite;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.Projects;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteLipSyncSegmentRepositoryTests
{
    [Fact]
    public async Task MigrateAsync_CreatesLipSyncSegmentsTable()
    {
        string projectRoot = CreateProjectRoot("LipSyncMigration");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            await database.InitializeAsync(TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database.DatabasePath);
            bool tableExists = await TableExistsAsync(connection, "LipSyncSegments");
            Assert.True(tableExists);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task SaveAllAsync_ThenGetByProjectAsync_RoundTrips()
    {
        string projectRoot = CreateProjectRoot("LipSyncByProject");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var repository = new SqliteLipSyncSegmentRepository(database);
            Guid projectId = Guid.NewGuid();
            Guid stageRunId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Seed project row so FK constraint is satisfied.
            var projectRepo = new SqliteProjectRepository(database);
            await projectRepo.InitializeAsync(
                new TrackdubProject(projectId, "Test", now, now),
                TestContext.Current.CancellationToken);

            var segment1 = new LipSyncSegment(
                SegmentId: Guid.NewGuid(),
                Status: LipSyncSegmentStatus.Aligned,
                SourceAlignmentId: null,
                TtsAlignmentId: "align-1",
                SourceDuration: TimeSpan.FromSeconds(2.5),
                TtsDuration: TimeSpan.FromSeconds(2.5),
                AlignedTtsDuration: TimeSpan.FromSeconds(2.6),
                PlanConfidence: 0.95,
                SkipReason: null,
                FailureReason: null,
                ProviderId: "test-provider",
                ModelId: "test-model",
                CreatedAtUtc: now);

            var segment2 = new LipSyncSegment(
                SegmentId: Guid.NewGuid(),
                Status: LipSyncSegmentStatus.SkippedNoPhonemes,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: null,
                SkipReason: "No phonemes found.",
                FailureReason: null,
                ProviderId: null,
                ModelId: null,
                CreatedAtUtc: now);

            await repository.SaveAllAsync(projectId, stageRunId, [segment1, segment2], TestContext.Current.CancellationToken);

            IReadOnlyList<LipSyncSegment> retrieved = await repository.GetByProjectAsync(projectId, TestContext.Current.CancellationToken);

            Assert.Equal(2, retrieved.Count);
            LipSyncSegment reloaded1 = Assert.Single(retrieved, s => s.SegmentId == segment1.SegmentId);
            Assert.Equal(LipSyncSegmentStatus.Aligned, reloaded1.Status);
            Assert.Equal("align-1", reloaded1.TtsAlignmentId);
            Assert.Equal(0.95, reloaded1.PlanConfidence);
            Assert.Equal(TimeSpan.FromSeconds(2.6), reloaded1.AlignedTtsDuration);
            Assert.Equal("test-provider", reloaded1.ProviderId);
            Assert.Equal("test-model", reloaded1.ModelId);

            LipSyncSegment reloaded2 = Assert.Single(retrieved, s => s.SegmentId == segment2.SegmentId);
            Assert.Equal(LipSyncSegmentStatus.SkippedNoPhonemes, reloaded2.Status);
            Assert.Equal("No phonemes found.", reloaded2.SkipReason);
            Assert.Null(reloaded2.AlignedTtsDuration);
            Assert.Null(reloaded2.PlanConfidence);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task SaveAllAsync_ThenGetByStageRunAsync_RoundTrips()
    {
        string projectRoot = CreateProjectRoot("LipSyncByStageRun");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var repository = new SqliteLipSyncSegmentRepository(database);
            Guid projectId = Guid.NewGuid();
            Guid stageRunId = Guid.NewGuid();
            Guid otherStageRunId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Seed project row so FK constraint is satisfied.
            var projectRepo = new SqliteProjectRepository(database);
            await projectRepo.InitializeAsync(
                new TrackdubProject(projectId, "Test", now, now),
                TestContext.Current.CancellationToken);

            var segment1 = new LipSyncSegment(
                Guid.NewGuid(), LipSyncSegmentStatus.Aligned,
                null, "align-a",
                TimeSpan.FromSeconds(1.0), TimeSpan.FromSeconds(1.0),
                TimeSpan.FromSeconds(1.1), 0.9,
                null, null, "prov", "mod", now);

            var segment2 = new LipSyncSegment(
                Guid.NewGuid(), LipSyncSegmentStatus.Partial,
                null, "align-b",
                TimeSpan.FromSeconds(3.0), TimeSpan.FromSeconds(3.0),
                TimeSpan.FromSeconds(3.2), 0.75,
                null, null, "prov", "mod", now);

            var segmentOtherRun = new LipSyncSegment(
                Guid.NewGuid(), LipSyncSegmentStatus.Failed,
                null, null,
                TimeSpan.Zero, TimeSpan.Zero,
                null, null,
                null, "Aligner failed.", null, null, now);

            await repository.SaveAllAsync(projectId, stageRunId, [segment1, segment2], TestContext.Current.CancellationToken);
            await repository.SaveAllAsync(projectId, otherStageRunId, [segmentOtherRun], TestContext.Current.CancellationToken);

            IReadOnlyList<LipSyncSegment> retrieved = await repository.GetByStageRunAsync(stageRunId, TestContext.Current.CancellationToken);

            Assert.Equal(2, retrieved.Count);
            Assert.Contains(retrieved, s => s.SegmentId == segment1.SegmentId);
            Assert.Contains(retrieved, s => s.SegmentId == segment2.SegmentId);
            Assert.DoesNotContain(retrieved, s => s.SegmentId == segmentOtherRun.SegmentId);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    [Fact]
    public async Task SaveAllAsync_WithDuplicateId_Replaces()
    {
        string projectRoot = CreateProjectRoot("LipSyncDuplicate");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var repository = new SqliteLipSyncSegmentRepository(database);
            Guid projectId = Guid.NewGuid();
            Guid stageRunId = Guid.NewGuid();
            Guid segmentId = Guid.NewGuid();
            DateTimeOffset now = DateTimeOffset.UtcNow;

            // Seed project row so FK constraint is satisfied.
            var projectRepo = new SqliteProjectRepository(database);
            await projectRepo.InitializeAsync(
                new TrackdubProject(projectId, "Test", now, now),
                TestContext.Current.CancellationToken);

            var original = new LipSyncSegment(
                segmentId, LipSyncSegmentStatus.Aligned,
                null, "align-orig",
                TimeSpan.FromSeconds(2.0), TimeSpan.FromSeconds(2.0),
                TimeSpan.FromSeconds(2.1), 0.88,
                null, null, "prov", "mod", now);

            var replacement = original with
            {
                Status = LipSyncSegmentStatus.Failed,
                TtsAlignmentId = null,
                AlignedTtsDuration = null,
                PlanConfidence = null,
                FailureReason = "Re-run failed."
            };

            await repository.SaveAllAsync(projectId, stageRunId, [original], TestContext.Current.CancellationToken);
            await repository.SaveAllAsync(projectId, stageRunId, [replacement], TestContext.Current.CancellationToken);

            IReadOnlyList<LipSyncSegment> retrieved = await repository.GetByProjectAsync(projectId, TestContext.Current.CancellationToken);

            LipSyncSegment reloaded = Assert.Single(retrieved);
            Assert.Equal(segmentId, reloaded.SegmentId);
            Assert.Equal(LipSyncSegmentStatus.Failed, reloaded.Status);
            Assert.Equal("Re-run failed.", reloaded.FailureReason);
            Assert.Null(reloaded.AlignedTtsDuration);
            Assert.Null(reloaded.PlanConfidence);
        }
        finally
        {
            DeleteDirectory(projectRoot);
        }
    }

    private static string CreateProjectRoot(string projectName) =>
        Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"),
            $"{projectName}.trackdub");

    private static async Task<SqliteConnection> OpenConnectionAsync(string databasePath)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT 1
            FROM sqlite_master
            WHERE type = 'table'
              AND name = $name
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$name", tableName);
        object? result = await command.ExecuteScalarAsync();
        return result is not null;
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
