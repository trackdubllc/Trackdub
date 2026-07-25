using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteUserBenchmarkDatabase
{
    public const string DatabaseFileName = "benchmarks.db";

    private readonly string databasePath;

    public SqliteUserBenchmarkDatabase(string userDataRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userDataRoot);
        databasePath = Path.Combine(Path.GetFullPath(userDataRoot), DatabaseFileName);
    }

    public string DatabasePath => databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS BenchmarkRuns (
                Id TEXT NOT NULL PRIMARY KEY,
                ModelId TEXT NOT NULL,
                ModelPath TEXT NOT NULL,
                ReportPath TEXT NOT NULL,
                Status TEXT NOT NULL,
                RequestedProvider TEXT NOT NULL,
                SelectedProvider TEXT NOT NULL,
                RunCount INTEGER NOT NULL,
                SupportsExecution INTEGER NOT NULL,
                ModelSizeBytes INTEGER NOT NULL,
                ColdLoadMilliseconds REAL NULL,
                WarmLatencyAverageMilliseconds REAL NULL,
                WarmLatencyMinimumMilliseconds REAL NULL,
                WarmLatencyMaximumMilliseconds REAL NULL,
                FailureReason TEXT NULL,
                GeneratedAtUtc TEXT NOT NULL,
                EvidenceId TEXT NULL,
                FingerprintHash TEXT NULL,
                Scenario TEXT NULL
            );

            CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_ModelId
                ON BenchmarkRuns (ModelId);

            CREATE INDEX IF NOT EXISTS IX_BenchmarkRuns_EvidenceId
                ON BenchmarkRuns (EvidenceId, Scenario);
            """;
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await SqliteProjectDatabase.EnableForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
        return connection;
    }
}
