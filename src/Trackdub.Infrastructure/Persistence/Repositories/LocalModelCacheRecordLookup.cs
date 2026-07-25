using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRecordLookup(LocalModelCacheRecordStore recordStore) : IModelCacheRecordLookup
{
    private readonly LocalModelCacheRecordStore recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));

    public LocalModelCacheRecord? Find(string modelId, string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        string normalizedRootPath = Path.GetFullPath(rootPath);
        IReadOnlyList<LocalModelCacheRecord> records = recordStore.LoadAsync().GetAwaiter().GetResult();

        return records.FirstOrDefault(candidate =>
            string.Equals(candidate.ModelId, modelId, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Path.GetFullPath(candidate.RootPath), normalizedRootPath, StringComparison.OrdinalIgnoreCase) &&
            !candidate.IntegrityFailed);
    }
}
