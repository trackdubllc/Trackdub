using System.Data.Common;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Migrations;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteProjectSpineTests
{
    [Fact]
    public async Task MigrationRunner_CreatesDatabaseFromScratch()
    {
        await using var database = new TemporarySqliteDatabase();

        await database.Migrator.MigrateAsync();

        Assert.True(File.Exists(database.DatabasePath));

        await using DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        IReadOnlyList<string> tables = await ReadTableNamesAsync(connection);

        Assert.Contains("Projects", tables);
        Assert.Contains("StageRuns", tables);
        Assert.Contains("Artifacts", tables);
        Assert.Contains("ModelCache", tables);
        Assert.Contains("BenchmarkRuns", tables);
        Assert.Contains("ConsentRecords", tables);
    }

    [Fact]
    public async Task MigrationRunner_AppliesVersionsInOrder()
    {
        await using var database = new TemporarySqliteDatabase();

        await database.Migrator.MigrateAsync();
        await database.Migrator.MigrateAsync();

        IReadOnlyList<SchemaVersionRecord> versions = await database.Migrator.GetAppliedVersionsAsync();

        int[] appliedVersions = versions.Select(version => version.Version).ToArray();
        int[] declaredVersions = SqliteMigrations.All
            .Select(migration => migration.Version)
            .ToArray();

        // Ensure the declared migration list is monotonically increasing
        for (int i = 1; i < declaredVersions.Length; i++)
        {
            Assert.True(declaredVersions[i] > declaredVersions[i - 1],
                $"Migration versions must be strictly increasing: version {declaredVersions[i]} at index {i} is not greater than {declaredVersions[i - 1]}");
        }

        // Ensure no duplicate versions in the declared migrations
        Assert.Equal(declaredVersions.Length, declaredVersions.Distinct().Count());

        // Verify applied versions match declared versions (in order)
        Assert.Equal(declaredVersions, appliedVersions);
    }

    [Fact]
    public async Task ProjectRepository_CanCreateAndReopenProject()
    {
        await using var database = new TemporarySqliteDatabase();
        await database.Migrator.MigrateAsync();

        ProjectRecord project = ProjectRecord.CreateNew("Demo", Path.Combine(Path.GetTempPath(), "trackdub-demo"), DateTimeOffset.UtcNow);
        var repository = new ProjectRecordRepository();

        await using (DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync())
        {
            await repository.CreateAsync(connection, project);
        }

        await using DbConnection reopenedConnection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        ProjectRecord? reopened = await repository.GetAsync(reopenedConnection, project.Id);

        Assert.NotNull(reopened);
        Assert.Equal(project.Name, reopened!.Name);
        Assert.Equal(project.RootPath, reopened.RootPath);
    }

    [Fact]
    public async Task StageRunRepository_CanCreateAndCompleteStageRun()
    {
        await using var database = new TemporarySqliteDatabase();
        await database.Migrator.MigrateAsync();

        ProjectRecord project = ProjectRecord.CreateNew("Demo", Path.Combine(Path.GetTempPath(), "trackdub-stage"), DateTimeOffset.UtcNow);
        StageRunRecord stageRun = StageRunRecord.Start(project.Id, "asr", DateTimeOffset.UtcNow)
            .WithRuntimeInfo("auto", "cpu", "onnx-community/whisper-tiny", "whisper-tiny-onnx", "int8", "bootstrap skipped");
        StageRunRecord completed = stageRun.Complete(DateTimeOffset.UtcNow.AddMinutes(1));

        var projectRepository = new ProjectRecordRepository();
        var stageRunRepository = new StageRunRepository();

        await using DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        await projectRepository.CreateAsync(connection, project);
        await stageRunRepository.CreateAsync(connection, stageRun);
        await stageRunRepository.CompleteAsync(connection, completed);

        StageRunRecord? reloaded = await stageRunRepository.GetAsync(connection, stageRun.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(StageRunStatus.Completed, reloaded!.Status);
        Assert.NotNull(reloaded.CompletedAtUtc);
        Assert.NotNull(reloaded.RuntimeInfo);
        Assert.Equal("cpu", reloaded.RuntimeInfo!.SelectedProvider);
        Assert.Equal("whisper-tiny-onnx", reloaded.RuntimeInfo.ModelAlias);
    }

    [Theory]
    [InlineData(StageRunStatus.Canceled)]
    [InlineData(StageRunStatus.Skipped)]
    [InlineData(StageRunStatus.PartiallyCompleted)]
    public async Task StageRunRepository_RoundTripsNewTerminalStatuses(StageRunStatus status)
    {
        await using var database = new TemporarySqliteDatabase();
        await database.Migrator.MigrateAsync();

        ProjectRecord project = ProjectRecord.CreateNew("Demo", Path.Combine(Path.GetTempPath(), "trackdub-stage"), DateTimeOffset.UtcNow);
        StageRunRecord stageRun = StageRunRecord.Start(project.Id, "asr", DateTimeOffset.UtcNow);
        StageRunRecord terminal = status switch
        {
            StageRunStatus.Canceled => stageRun.Cancel(DateTimeOffset.UtcNow.AddMinutes(1), "Canceled by user."),
            StageRunStatus.Skipped => stageRun.Skip(DateTimeOffset.UtcNow.AddMinutes(1), "Skipped by configuration."),
            StageRunStatus.PartiallyCompleted => stageRun.PartiallyComplete(DateTimeOffset.UtcNow.AddMinutes(1), "Some outputs were produced."),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, "Unsupported test status.")
        };

        var projectRepository = new ProjectRecordRepository();
        var stageRunRepository = new StageRunRepository();

        await using DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        await projectRepository.CreateAsync(connection, project);
        await stageRunRepository.CreateAsync(connection, stageRun);
        await stageRunRepository.CompleteAsync(connection, terminal);

        StageRunRecord? reloaded = await stageRunRepository.GetAsync(connection, stageRun.Id);

        Assert.NotNull(reloaded);
        Assert.Equal(status, reloaded!.Status);
        Assert.NotNull(reloaded.CompletedAtUtc);
        Assert.False(string.IsNullOrWhiteSpace(reloaded.FailureReason));
    }

    [Fact]
    public async Task ArtifactRepository_CanRegisterArtifactWithProvenance()
    {
        await using var database = new TemporarySqliteDatabase();
        await database.Migrator.MigrateAsync();

        ProjectRecord project = ProjectRecord.CreateNew("Demo", Path.Combine(Path.GetTempPath(), "trackdub-artifact"), DateTimeOffset.UtcNow);
        StageRunRecord stageRun = StageRunRecord.Start(project.Id, "translation", DateTimeOffset.UtcNow);
        ArtifactRecord artifact = ArtifactRecord.Register(
            project.Id,
            stageRun.Id,
            "transcript",
            Path.Combine("artifacts", "transcript.json"),
            "abc123",
            "translation-stage",
            DateTimeOffset.UtcNow);

        var projectRepository = new ProjectRecordRepository();
        var stageRunRepository = new StageRunRepository();
        var artifactRepository = new ArtifactRepository();

        await using DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        await projectRepository.CreateAsync(connection, project);
        await stageRunRepository.CreateAsync(connection, stageRun);
        await artifactRepository.RegisterAsync(connection, artifact);

        IReadOnlyList<ArtifactRecord> artifacts = await artifactRepository.ListByProjectAsync(connection, project.Id);

        ArtifactRecord stored = Assert.Single(artifacts);
        Assert.Equal("abc123", stored.ContentHash);
        Assert.Equal("translation-stage", stored.Provenance);
        Assert.Equal(Path.Combine("artifacts", "transcript.json"), stored.RelativePath);
    }

    [Fact]
    public async Task Repositories_RollbackTransactionLeavesNoRows()
    {
        await using var database = new TemporarySqliteDatabase();
        await database.Migrator.MigrateAsync();

        ProjectRecord project = ProjectRecord.CreateNew("Demo", Path.Combine(Path.GetTempPath(), "trackdub-rollback"), DateTimeOffset.UtcNow);
        ArtifactRecord artifact = ArtifactRecord.Register(
            project.Id,
            null,
            "artifact",
            Path.Combine("artifacts", "rollback.json"),
            "deadbeef",
            "rollback-test",
            DateTimeOffset.UtcNow);

        var projectRepository = new ProjectRecordRepository();
        var artifactRepository = new ArtifactRepository();

        await using (DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync())
        await using (DbTransaction transaction = await connection.BeginTransactionAsync())
        {
            await projectRepository.CreateAsync(connection, project, transaction);
            await artifactRepository.RegisterAsync(connection, artifact, transaction);
            await transaction.RollbackAsync();
        }

        await using DbConnection verificationConnection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        ProjectRecord? reloadedProject = await projectRepository.GetAsync(verificationConnection, project.Id);
        IReadOnlyList<ArtifactRecord> artifacts = await artifactRepository.ListByProjectAsync(verificationConnection, project.Id);

        Assert.Null(reloadedProject);
        Assert.Empty(artifacts);
    }

    [Fact]
    public async Task ModelCacheRepository_CanUpsertRecord()
    {
        await using var database = new TemporarySqliteDatabase();
        await database.Migrator.MigrateAsync();

        LocalModelCacheRecord record = new("onnx-community/silero-vad", @"D:\models\silero-vad", "main", "abc123", DateTimeOffset.UtcNow);
        var repository = new ModelCacheRepository();

        await using DbConnection connection = await database.ConnectionFactory.CreateOpenConnectionAsync();
        await repository.UpsertAsync(connection, record);

        LocalModelCacheRecord? reloaded = await repository.GetAsync(connection, record.ModelId);

        Assert.NotNull(reloaded);
        Assert.Equal(record.RootPath, reloaded!.RootPath);
        Assert.Equal(record.Sha256, reloaded.Sha256);
    }

    private static async Task<IReadOnlyList<string>> ReadTableNamesAsync(DbConnection connection)
    {
        await using DbCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT name
            FROM sqlite_master
            WHERE type = 'table'
              AND name NOT LIKE 'sqlite_%'
            ORDER BY name;
            """;

        var tables = new List<string>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            tables.Add(reader.GetString(0));
        }

        return tables;
    }

    private sealed class TemporarySqliteDatabase : IAsyncDisposable
    {
        public TemporarySqliteDatabase()
        {
            DatabasePath = Path.Combine(Path.GetTempPath(), $"trackdub-{Guid.NewGuid():N}.db");
            ConnectionFactory = new SqliteConnectionFactory(DatabasePath);
            Migrator = new SqliteDatabaseMigrator(ConnectionFactory);
        }

        public string DatabasePath { get; }

        public SqliteConnectionFactory ConnectionFactory { get; }

        public SqliteDatabaseMigrator Migrator { get; }

        public async ValueTask DisposeAsync()
        {
            await Task.Run(() =>
            {
                DeleteIfPresent(DatabasePath);
                DeleteIfPresent($"{DatabasePath}-wal");
                DeleteIfPresent($"{DatabasePath}-shm");
            }).ConfigureAwait(false);
        }

        private static void DeleteIfPresent(string path)
        {
            for (int attempt = 0; attempt < 5; attempt++)
            {
                try
                {
                    if (File.Exists(path))
                    {
                        File.Delete(path);
                    }

                    return;
                }
                catch (IOException) when (attempt < 4)
                {
                    Thread.Sleep(50);
                }
            }
        }
    }
}
