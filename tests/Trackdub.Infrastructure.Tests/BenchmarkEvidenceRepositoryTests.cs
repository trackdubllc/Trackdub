using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Domain.Projects;
using Trackdub.Infrastructure.Logging;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class BenchmarkEvidenceRepositoryTests
{
    [Fact]
    public async Task Controlled_report_round_trips_without_private_path_and_is_not_pruned()
    {
        string root = NewRoot();
        try
        {
            var repository = new BenchmarkEvidenceRepository(new SqliteUserBenchmarkDatabase(root));
            Guid benchmarkId = Guid.NewGuid();
            await repository.SaveAsync(new BenchmarkEvidenceReport
            {
                RunId = benchmarkId,
                Kind = BenchmarkEvidenceKind.Benchmark,
                Scenario = "asr",
                RunMode = "fresh-process",
                Status = BenchmarkEvidenceStatus.Completed,
                CompletedAtUtc = DateTimeOffset.UtcNow.AddDays(-100),
                FixtureSha256 = new string('a', 64),
                RequestedModel = "onnx-community/whisper-tiny",
                Reason = @"Read D:\private\source.wav",
                TimingsMilliseconds = new Dictionary<string, double?> { ["stage"] = 42.5 }
            });
            await repository.SaveAsync(Observation(Guid.NewGuid(), DateTimeOffset.UtcNow));

            BenchmarkEvidenceReport loaded = Assert.IsType<BenchmarkEvidenceReport>(
                await repository.GetAsync(benchmarkId));
            Assert.Equal(42.5, loaded.TimingsMilliseconds["stage"]);
            Assert.Equal("onnx-community/whisper-tiny", loaded.RequestedModel);
            Assert.DoesNotContain(@"D:\private", loaded.Reason);
            Assert.Single(await repository.ListRecentAsync(BenchmarkEvidenceKind.Benchmark, 10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Old_observations_are_pruned_but_explicit_reports_remain()
    {
        string root = NewRoot();
        try
        {
            var repository = new BenchmarkEvidenceRepository(new SqliteUserBenchmarkDatabase(root));
            Guid oldId = Guid.NewGuid();
            await repository.SaveAsync(Observation(oldId, DateTimeOffset.UtcNow.AddDays(-91)));
            await repository.SaveAsync(Observation(Guid.NewGuid(), DateTimeOffset.UtcNow));
            Assert.Null(await repository.GetAsync(oldId));
            Assert.Single(await repository.ListRecentAsync(BenchmarkEvidenceKind.Observation, 10));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Terminal_stage_write_survives_evidence_failure()
    {
        string root = NewRoot();
        try
        {
            var database = new SqliteProjectDatabase(Path.Combine(root, "project.trackdub"));
            var projectStore = new SqliteProjectRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Evidence failure", now, now);
            await projectStore.InitializeAsync(project, TestContext.Current.CancellationToken);
            var store = new ObservedProjectStageRunStore(
                new SqliteProjectStageRunStore(database), new ThrowingEvidenceRepository(), new DebugApplicationLogger());
            StageRunRecord started = StageRunRecord.Start(project.Id, "asr", now);
            await store.CreateAsync(started, TestContext.Current.CancellationToken);
            await store.UpdateAsync(started.Complete(DateTimeOffset.UtcNow), TestContext.Current.CancellationToken);
            StageRunRecord saved = Assert.Single(await store.ListByProjectAsync(project.Id, TestContext.Current.CancellationToken));
            Assert.Equal(StageRunStatus.Completed, saved.Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public async Task Terminal_stage_creates_local_observation_with_measured_duration()
    {
        string root = NewRoot();
        try
        {
            var database = new SqliteProjectDatabase(Path.Combine(root, "project.trackdub"));
            var projectStore = new SqliteProjectRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Observation", now, now);
            await projectStore.InitializeAsync(project, TestContext.Current.CancellationToken);
            var evidence = new BenchmarkEvidenceRepository(new SqliteUserBenchmarkDatabase(Path.Combine(root, "user")));
            var store = new ObservedProjectStageRunStore(
                new SqliteProjectStageRunStore(database), evidence, new DebugApplicationLogger());
            StageRunRecord started = StageRunRecord.Start(project.Id, "tts", now);
            await store.CreateAsync(started, TestContext.Current.CancellationToken);
            await store.UpdateAsync(started.Complete(DateTimeOffset.UtcNow).WithRuntimeInfo("auto", "cpu", "kokoro"),
                TestContext.Current.CancellationToken);

            BenchmarkEvidenceReport report = Assert.IsType<BenchmarkEvidenceReport>(await evidence.GetAsync(started.Id));
            Assert.Equal(BenchmarkEvidenceKind.Observation, report.Kind);
            Assert.Equal("cpu", report.ActualProvider);
            Assert.True(report.TimingsMilliseconds["stage"] >= 0);
            Assert.Equal(BenchmarkEvidenceStatus.Completed, Assert.Single(report.Stages).Status);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string NewRoot() => Path.Combine(Path.GetTempPath(), "Trackdub.Evidence.Tests", Guid.NewGuid().ToString("N"));

    private static BenchmarkEvidenceReport Observation(Guid id, DateTimeOffset completedAt) => new()
    {
        RunId = id,
        Kind = BenchmarkEvidenceKind.Observation,
        Scenario = "asr",
        RunMode = "ordinary-stage",
        Status = BenchmarkEvidenceStatus.Completed,
        CompletedAtUtc = completedAt
    };

    private sealed class ThrowingEvidenceRepository : IBenchmarkEvidenceRepository
    {
        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default) =>
            throw new IOException("Storage unavailable");
        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BenchmarkEvidenceReport?>(null);
        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
            BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>([]);
    }
}
