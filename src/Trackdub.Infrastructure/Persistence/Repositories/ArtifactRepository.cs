using System.Data.Common;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class ArtifactRepository : IArtifactRepository
{
    public async Task RegisterAsync(
        DbConnection connection,
        ArtifactRecord artifact,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO Artifacts (Id, ProjectId, StageRunId, Kind, RelativePath, ContentHash, Provenance, CreatedAtUtc)
            VALUES (@Id, @ProjectId, @StageRunId, @Kind, @RelativePath, @ContentHash, @Provenance, @CreatedAtUtc);
            """,
            new
            {
                Id = SqliteValueConverters.ToDbValue(artifact.Id),
                ProjectId = SqliteValueConverters.ToDbValue(artifact.ProjectId),
                StageRunId = artifact.StageRunId is Guid stageRunId ? SqliteValueConverters.ToDbValue(stageRunId) : null,
                artifact.Kind,
                artifact.RelativePath,
                ContentHash = artifact.ContentHash,
                artifact.Provenance,
                CreatedAtUtc = SqliteValueConverters.ToDbValue(artifact.CreatedAtUtc)
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ArtifactRecord>> ListByProjectAsync(
        DbConnection connection,
        Guid projectId,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ArtifactRow> rows = (await connection.QueryAsync<ArtifactRow>(new CommandDefinition(
            """
            SELECT Id, ProjectId, StageRunId, Kind, RelativePath, ContentHash, Provenance, CreatedAtUtc
            FROM Artifacts
            WHERE ProjectId = @ProjectId
            ORDER BY CreatedAtUtc, Id;
            """,
            new { ProjectId = SqliteValueConverters.ToDbValue(projectId) },
            cancellationToken: cancellationToken)).ConfigureAwait(false)).AsList();

        return rows
            .Select(row => new ArtifactRecord(
                SqliteValueConverters.ParseGuid(row.Id),
                SqliteValueConverters.ParseGuid(row.ProjectId),
                string.IsNullOrWhiteSpace(row.StageRunId) ? null : SqliteValueConverters.ParseGuid(row.StageRunId),
                row.Kind,
                row.RelativePath,
                row.ContentHash,
                row.Provenance,
                SqliteValueConverters.ParseDateTimeOffset(row.CreatedAtUtc)))
            .ToArray();
    }

    private sealed record ArtifactRow(
        string Id,
        string ProjectId,
        string? StageRunId,
        string Kind,
        string RelativePath,
        string ContentHash,
        string Provenance,
        string CreatedAtUtc);
}
