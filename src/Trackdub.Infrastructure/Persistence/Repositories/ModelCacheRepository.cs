using System.Data.Common;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Dapper;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class ModelCacheRepository : IModelCacheRepository
{
    public async Task UpsertAsync(
        DbConnection connection,
        LocalModelCacheRecord record,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default)
    {
        await connection.ExecuteAsync(new CommandDefinition(
            """
            INSERT INTO ModelCache (ModelId, RootPath, Revision, Sha256, CachedAtUtc)
            VALUES (@ModelId, @RootPath, @Revision, @Sha256, @CachedAtUtc)
            ON CONFLICT(ModelId) DO UPDATE SET
                RootPath = excluded.RootPath,
                Revision = excluded.Revision,
                Sha256 = excluded.Sha256,
                CachedAtUtc = excluded.CachedAtUtc;
            """,
            new
            {
                record.ModelId,
                record.RootPath,
                record.Revision,
                record.Sha256,
                CachedAtUtc = SqliteValueConverters.ToDbValue(record.CachedAtUtc)
            },
            transaction,
            cancellationToken: cancellationToken)).ConfigureAwait(false);
    }

    public async Task<LocalModelCacheRecord?> GetAsync(
        DbConnection connection,
        string modelId,
        CancellationToken cancellationToken = default)
    {
        ModelCacheRow? row = await connection.QuerySingleOrDefaultAsync<ModelCacheRow>(new CommandDefinition(
            """
            SELECT ModelId, RootPath, Revision, Sha256, CachedAtUtc
            FROM ModelCache
            WHERE ModelId = @ModelId;
            """,
            new { ModelId = modelId },
            cancellationToken: cancellationToken)).ConfigureAwait(false);

        return row is null
            ? null
            : new LocalModelCacheRecord(
                row.ModelId,
                row.RootPath,
                row.Revision,
                row.Sha256,
                SqliteValueConverters.ParseDateTimeOffset(row.CachedAtUtc));
    }

    private sealed record ModelCacheRow(
        string ModelId,
        string RootPath,
        string Revision,
        string Sha256,
        string CachedAtUtc);
}
