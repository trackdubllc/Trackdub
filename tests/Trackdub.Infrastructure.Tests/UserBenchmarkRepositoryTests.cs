using Dapper;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Xunit;

namespace Trackdub.Infrastructure.Tests;

public sealed class UserBenchmarkRepositoryTests
{
    [Fact]
    public async Task AddAsync_round_trips_profiler_runs_by_evidence_id()
    {
        string userRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new SqliteUserBenchmarkDatabase(userRoot);
            var repository = new UserBenchmarkRepository(database);
            Guid evidenceId = Guid.Parse("11111111-2222-3333-4444-555555555555");
            string modelId = UserBenchmarkRepository.BuildProfilerModelId(evidenceId, "abc123", "vad");
            DateTimeOffset generatedAt = DateTimeOffset.Parse("2026-06-12T12:00:00Z");

            var run = new BenchmarkRunRecord(
                Guid.Parse("99999999-8888-7777-6666-555555555555"),
                modelId,
                @"C:\models\vad.onnx",
                @"C:\reports\vad-report.json",
                BenchmarkStatus.Completed,
                "auto",
                "dml",
                3,
                true,
                1024,
                120.5,
                40.1,
                38.0,
                44.0,
                null,
                generatedAt);

            await repository.AddAsync(run, TestContext.Current.CancellationToken);

            Assert.True(await repository.ContainsEvidenceAsync(evidenceId, TestContext.Current.CancellationToken));

            IReadOnlyList<BenchmarkRunRecord> byEvidence =
                await repository.ListByEvidenceIdAsync(evidenceId, TestContext.Current.CancellationToken);
            BenchmarkRunRecord reloaded = Assert.Single(byEvidence);
            Assert.Equal(run.Id, reloaded.Id);
            Assert.Equal(modelId, reloaded.ModelId);
            Assert.Equal(40.1, reloaded.WarmLatencyAverageMilliseconds);

            IReadOnlyList<BenchmarkRunRecord> recent =
                await repository.ListRecentAsync(5, TestContext.Current.CancellationToken);
            Assert.Contains(recent, item => item.Id == run.Id);

            await repository.AddAsync(run with { WarmLatencyAverageMilliseconds = 41.0 }, TestContext.Current.CancellationToken);
            BenchmarkRunRecord updated = Assert.Single(
                await repository.ListByEvidenceIdAsync(evidenceId, TestContext.Current.CancellationToken));
            Assert.Equal(41.0, updated.WarmLatencyAverageMilliseconds);
        }
        finally
        {
            string dbPath = Path.Combine(userRoot, SqliteUserBenchmarkDatabase.DatabaseFileName);
            if (File.Exists(dbPath))
            {
                File.Delete(dbPath);
            }

            if (Directory.Exists(userRoot))
            {
                Directory.Delete(userRoot, recursive: true);
            }
        }
    }

    [Fact]
    public void ParseProfilerMetadata_ReturnsNullForNonProfilerModelIds()
    {
        (Guid? evidenceId, string? fingerprintHash, string? scenario) =
            UserBenchmarkRepository.ParseProfilerMetadata("whisper-large-v3");

        Assert.Null(evidenceId);
        Assert.Null(fingerprintHash);
        Assert.Null(scenario);
    }
    [Fact]
    public async Task ListRecentAsync_maps_unknown_status_to_failed()
    {
        string userRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var database = new SqliteUserBenchmarkDatabase(userRoot);
            var repository = new UserBenchmarkRepository(database);
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            await using Microsoft.Data.Sqlite.SqliteConnection connection =
                await database.OpenConnectionAsync(TestContext.Current.CancellationToken);
            Guid runId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
            await connection.ExecuteAsync(
                """
                INSERT INTO BenchmarkRuns (
                    Id, ModelId, ModelPath, ReportPath, Status, RequestedProvider, SelectedProvider,
                    RunCount, SupportsExecution, ModelSizeBytes, GeneratedAtUtc)
                VALUES (
                    @Id, @ModelId, @ModelPath, @ReportPath, @Status, @RequestedProvider, @SelectedProvider,
                    @RunCount, @SupportsExecution, @ModelSizeBytes, @GeneratedAtUtc)
                """,
                new
                {
                    Id = runId.ToString("D"),
                    ModelId = "whisper-large-v3",
                    ModelPath = "model.onnx",
                    ReportPath = "report.json",
                    Status = "corrupted-status",
                    RequestedProvider = "auto",
                    SelectedProvider = "cpu",
                    RunCount = 1,
                    SupportsExecution = 0,
                    ModelSizeBytes = 0L,
                    GeneratedAtUtc = DateTimeOffset.UtcNow.ToString("O"),
                });

            BenchmarkRunRecord reloaded = Assert.Single(
                await repository.ListRecentAsync(1, TestContext.Current.CancellationToken));

            Assert.Equal(runId, reloaded.Id);
            Assert.Equal(BenchmarkStatus.Failed, reloaded.Status);
        }
        finally
        {
            if (Directory.Exists(userRoot))
            {
                Directory.Delete(userRoot, recursive: true);
            }
        }
    }
}
