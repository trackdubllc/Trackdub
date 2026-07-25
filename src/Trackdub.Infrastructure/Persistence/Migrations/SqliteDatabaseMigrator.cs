using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Migrations;

public sealed class SqliteDatabaseMigrator(IDbConnectionFactory connectionFactory) : IDatabaseMigrator
{
    public async Task MigrateAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);

        HashSet<int> appliedVersions = (await LoadAppliedVersionNumbersAsync(connection, cancellationToken).ConfigureAwait(false)).ToHashSet();
        foreach (SqliteMigration migration in SqliteMigrations.All.OrderBy(migration => migration.Version))
        {
            if (appliedVersions.Contains(migration.Version))
            {
                continue;
            }

            await using var transaction = await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await connection.ExecuteAsync(new CommandDefinition(
                    migration.Sql,
                    transaction: transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await connection.ExecuteAsync(new CommandDefinition(
                    """
                    INSERT INTO SchemaVersion (Version, Name, AppliedAtUtc)
                    VALUES (@Version, @Name, @AppliedAtUtc);
                    """,
                    new
                    {
                        migration.Version,
                        migration.Name,
                        AppliedAtUtc = Sqlite.SqliteValueConverters.ToDbValue(DateTimeOffset.UtcNow)
                    },
                    transaction,
                    cancellationToken: cancellationToken)).ConfigureAwait(false);

                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                await transaction.RollbackAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
        }
    }

    public async Task<IReadOnlyList<SchemaVersionRecord>> GetAppliedVersionsAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await connectionFactory.CreateOpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        await EnsureSchemaVersionTableAsync(connection, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<SchemaVersionRow> rows = (await connection.QueryAsync<SchemaVersionRow>(new CommandDefinition(
            """
            SELECT Version, Name, AppliedAtUtc
            FROM SchemaVersion
            ORDER BY Version;
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows
            .Select(row => new SchemaVersionRecord(
                checked((int)row.Version),
                row.Name,
                Sqlite.SqliteValueConverters.ParseDateTimeOffset(row.AppliedAtUtc)))
            .ToArray();
    }

    private static async Task EnsureSchemaVersionTableAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            CREATE TABLE IF NOT EXISTS SchemaVersion (
                Version INTEGER PRIMARY KEY,
                Name TEXT NOT NULL,
                AppliedAtUtc TEXT NOT NULL
            );
            """,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    private static async Task<IReadOnlyList<int>> LoadAppliedVersionNumbersAsync(System.Data.Common.DbConnection connection, CancellationToken cancellationToken)
    {
        IReadOnlyList<int> versions = (await connection.QueryAsync<int>(new CommandDefinition(
            "SELECT Version FROM SchemaVersion ORDER BY Version;",
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return versions;
    }

    private sealed record SchemaVersionRow(long Version, string Name, string AppliedAtUtc);
}
