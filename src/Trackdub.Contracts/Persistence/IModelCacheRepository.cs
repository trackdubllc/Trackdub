using System.Data.Common;
using Trackdub.Domain;

namespace Trackdub.Contracts.Persistence;

public interface IModelCacheRepository
{
    Task UpsertAsync(
        DbConnection connection,
        LocalModelCacheRecord record,
        DbTransaction? transaction = null,
        CancellationToken cancellationToken = default);

    Task<LocalModelCacheRecord?> GetAsync(
        DbConnection connection,
        string modelId,
        CancellationToken cancellationToken = default);
}
