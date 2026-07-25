using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime.Planning;

public sealed class LocalModelCacheInventory(LocalModelCacheRecordStore recordStore) : IModelCacheInventory
{
    private readonly LocalModelCacheRecordStore recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));

    public async Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
        await recordStore.LoadAsync(cancellationToken).ConfigureAwait(false);
}
