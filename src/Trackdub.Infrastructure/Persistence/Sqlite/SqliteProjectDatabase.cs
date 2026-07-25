using Trackdub.Contracts.Projects;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteProjectDatabase
{
    private readonly string databasePath;

    public SqliteProjectDatabase(string projectRootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        databasePath = Path.Combine(Path.GetFullPath(projectRootPath), ProjectArtifactPaths.DatabaseFileName);
    }

    public string DatabasePath => databasePath;

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        string? backupPath = null;

        // If the database file already exists, check for pending migrations before touching it.
        // When migrations are pending we create a timestamped backup so the user can recover if
        // the migration fails or leaves the database in an unexpected state.
        if (File.Exists(databasePath))
        {
            try
            {
                bool hasPending;
                await using (SqliteConnection preCheckConnection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false))
                {
                    hasPending = await SqliteProjectSchemaMigrations
                        .HasPendingMigrationsAsync(preCheckConnection, cancellationToken)
                        .ConfigureAwait(false);
                }

                if (hasPending)
                {
                    backupPath = CreatePreMigrationBackup(databasePath);
                }
            }
            catch (SqliteException ex) when (IsFileCorruptionError(ex))
            {
                throw new ProjectDatabaseCorruptedException(databasePath, ex.Message, ex, backupPath: null);
            }
        }

        try
        {
            await using SqliteConnection connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await SqliteProjectSchemaMigrations.MigrateAsync(connection, cancellationToken).ConfigureAwait(false);
            await ValidateIntegrityAsync(connection, backupPath, cancellationToken).ConfigureAwait(false);
        }
        catch (SqliteException ex) when (IsFileCorruptionError(ex))
        {
            throw new ProjectDatabaseCorruptedException(databasePath, ex.Message, ex, backupPath);
        }
    }

    public async Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            // Direct opens are used by tests/tools and migration bootstrap; scoped repository
            // access uses ScopedSqliteConnectionProvider for pooled reuse.
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        try
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await EnableForeignKeysAsync(connection, cancellationToken).ConfigureAwait(false);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    internal static async Task EnableForeignKeysAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using SqliteCommand pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        await pragmaCommand.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Copies the database file to a timestamped backup path in the same directory.
    /// Returns the backup path so it can be included in recovery error messages.
    /// </summary>
    internal static string CreatePreMigrationBackup(string sourceDatabasePath)
    {
        IOException? lastCopyError = null;

        for (int attempt = 0; attempt < 5; attempt++)
        {
            string timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddTHHmmssfffZ");
            string backupPath = $"{sourceDatabasePath}.{timestamp}.{Guid.NewGuid():N}.bak";

            try
            {
                File.Copy(sourceDatabasePath, backupPath, overwrite: false);
                return backupPath;
            }
            catch (IOException ex)
            {
                lastCopyError = ex;

                try
                {
                    if (File.Exists(backupPath))
                    {
                        File.Delete(backupPath);
                    }
                }
                catch (IOException)
                {
                }
            }
        }

        throw new IOException(
            $"Failed to create a pre-migration backup for '{sourceDatabasePath}'.",
            lastCopyError);
    }

    private async Task ValidateIntegrityAsync(SqliteConnection connection, string? backupPath, CancellationToken cancellationToken)
    {
        // integrity_check(1) stops after the first error, keeping startup fast for healthy databases.
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "PRAGMA integrity_check(1);";
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        string checkResult = result as string ?? "unknown error";

        if (!string.Equals(checkResult, "ok", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProjectDatabaseCorruptedException(databasePath, checkResult, backupPath);
        }
    }

    /// <summary>
    /// Returns <see langword="true"/> for SQLite errors that indicate the database file itself is
    /// corrupt or not a valid SQLite database (as opposed to recoverable query/constraint errors).
    /// </summary>
    private static bool IsFileCorruptionError(SqliteException ex) =>
        ex.SqliteErrorCode is
            11 /* SQLITE_CORRUPT   — internal database structure is damaged   */ or
            26 /* SQLITE_NOTADB    — file is not a recognisable SQLite database */;
}

