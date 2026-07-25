using Trackdub.Contracts;
using Trackdub.Domain.Projects;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class SqliteProjectRepository(
    SqliteProjectDatabase database,
    IScopedConnectionProvider? scopedConnectionProvider = null)
    : IProjectRepository
{
    public async Task InitializeAsync(TrackdubProject project, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            INSERT INTO projects (id, name, created_at_utc, updated_at_utc)
            VALUES ($id, $name, $createdAtUtc, $updatedAtUtc);
            """;
        command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$createdAtUtc", project.CreatedAtUtc.UtcDateTime);
        command.Parameters.AddWithValue("$updatedAtUtc", project.UpdatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task UpdateAsync(TrackdubProject project, CancellationToken cancellationToken)
    {
        await database.InitializeAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            UPDATE projects
            SET name = $name,
                updated_at_utc = $updatedAtUtc
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue("$id", project.Id.ToString("D"));
        command.Parameters.AddWithValue("$name", project.Name);
        command.Parameters.AddWithValue("$updatedAtUtc", project.UpdatedAtUtc.UtcDateTime);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<TrackdubProject?> GetAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(database.DatabasePath))
        {
            return null;
        }

        await using SqliteConnectionLease connectionLease = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        SqliteConnection connection = connectionLease.Connection;
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT id, name, created_at_utc, updated_at_utc
            FROM projects
            LIMIT 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return new TrackdubProject(
            Guid.Parse(reader.GetString(0)),
            reader.GetString(1),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(2), DateTimeKind.Utc)),
            new DateTimeOffset(DateTime.SpecifyKind(reader.GetDateTime(3), DateTimeKind.Utc)));
    }

    private Task<SqliteConnectionLease> OpenConnectionAsync(CancellationToken cancellationToken) =>
        SqliteConnectionLease.OpenAsync(database, scopedConnectionProvider, cancellationToken);
}
