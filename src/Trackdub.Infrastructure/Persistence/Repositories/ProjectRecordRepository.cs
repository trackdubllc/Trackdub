using System.Data.Common;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class ProjectRecordRepository : IProjectRepository
{
    public async Task CreateAsync(
        DbConnection connection,
        ProjectRecord project,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Projects (Id, Name, RootPath, CreatedAtUtc, UpdatedAtUtc)
            VALUES (@Id, @Name, @RootPath, @CreatedAtUtc, @UpdatedAtUtc);
            """,
            new
            {
                Id = SqliteValueConverters.ToDbValue(project.Id),
                project.Name,
                project.RootPath,
                CreatedAtUtc = SqliteValueConverters.ToDbValue(project.CreatedAtUtc),
                UpdatedAtUtc = SqliteValueConverters.ToDbValue(project.UpdatedAtUtc)
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<ProjectRecord?> GetAsync(
        DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        ProjectRow? row = await connection.QuerySingleOrDefaultAsync<ProjectRow>(new CommandDefinition(
            """
            SELECT Id, Name, RootPath, CreatedAtUtc, UpdatedAtUtc
            FROM Projects
            WHERE Id = @Id;
            """,
            new { Id = SqliteValueConverters.ToDbValue(projectId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new ProjectRecord(
                SqliteValueConverters.ParseGuid(row.Id),
                row.Name,
                row.RootPath,
                SqliteValueConverters.ParseDateTimeOffset(row.CreatedAtUtc),
                SqliteValueConverters.ParseDateTimeOffset(row.UpdatedAtUtc));
    }

    private sealed record ProjectRow(
        string Id,
        string Name,
        string RootPath,
        string CreatedAtUtc,
        string UpdatedAtUtc);
}
