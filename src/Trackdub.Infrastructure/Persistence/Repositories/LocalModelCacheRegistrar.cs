using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelCacheRegistrar(LocalModelCacheRecordStore recordStore) : IModelCacheRegistrar
{
    private readonly LocalModelCacheRecordStore recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));

    public async Task RegisterAsync(
        LocalModelCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(record);

        await recordStore
            .MutateAsync(
                existingRecords =>
                {
                    LocalModelCacheRecord? existing = existingRecords.FirstOrDefault(candidate =>
                        string.Equals(candidate.ModelId, record.ModelId, StringComparison.OrdinalIgnoreCase) &&
                        LocalModelCacheRecordStore.RootsEqual(candidate.RootPath, record.RootPath));

                    LocalModelCacheRecord merged = existing is null
                        ? record
                        : record with { Variants = LocalModelCacheRecordStore.CompatibleVariants(record, existing.Variants.Concat(record.Variants)) };

                    return existingRecords
                        .Where(candidate =>
                            !string.Equals(candidate.ModelId, record.ModelId, StringComparison.OrdinalIgnoreCase) ||
                            !LocalModelCacheRecordStore.RootsEqual(candidate.RootPath, record.RootPath))
                        .Append(merged)
                        .ToArray();
                },
                cancellationToken)
            .ConfigureAwait(false);
    }
}
