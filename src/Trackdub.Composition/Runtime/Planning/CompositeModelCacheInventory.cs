using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime.Planning;

public sealed class CompositeModelCacheInventory : IModelCacheInventory
{
    private readonly IReadOnlyList<IModelCacheInventory> inventories;

    public CompositeModelCacheInventory(params IModelCacheInventory[] inventories)
    {
        ArgumentNullException.ThrowIfNull(inventories);
        if (inventories.Length == 0)
        {
            throw new ArgumentException("At least one inventory is required.", nameof(inventories));
        }

        this.inventories = inventories;
    }

    public async Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        var records = new List<LocalModelCacheRecord>();
        foreach (IModelCacheInventory inventory in inventories)
        {
            IReadOnlyList<LocalModelCacheRecord> loaded = await inventory.LoadAsync(cancellationToken).ConfigureAwait(false);
            records.AddRange(loaded);
        }

        return records;
    }
}
