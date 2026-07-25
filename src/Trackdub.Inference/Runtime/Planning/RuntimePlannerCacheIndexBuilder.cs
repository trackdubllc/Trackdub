using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

internal sealed class RuntimePlannerCacheIndexBuilder
{
    private readonly IModelCacheInventory modelCacheInventory;
    private readonly TimeProvider timeProvider;

    // TTL-based cache for the built index (model cache state rarely changes during a pipeline run).
    private IReadOnlyDictionary<string, IReadOnlyList<LocalModelCacheRecord>>? _cachedIndex;
    private DateTimeOffset _cachedAtUtc;
    private readonly object _cacheLock = new();
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    public RuntimePlannerCacheIndexBuilder(IModelCacheInventory modelCacheInventory, TimeProvider? timeProvider = null)
    {
        this.modelCacheInventory = modelCacheInventory ?? throw new ArgumentNullException(nameof(modelCacheInventory));
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyDictionary<string, IReadOnlyList<LocalModelCacheRecord>>> BuildAsync(CancellationToken cancellationToken)
    {
        lock (_cacheLock)
        {
            if (_cachedIndex is not null && timeProvider.GetUtcNow() - _cachedAtUtc < CacheTtl)
            {
                return _cachedIndex;
            }
        }

        IReadOnlyList<LocalModelCacheRecord> records = await modelCacheInventory.LoadAsync(cancellationToken).ConfigureAwait(false);
        var index = records
            .GroupBy(record => record.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => (IReadOnlyList<LocalModelCacheRecord>)group
                    .OrderByDescending(record => record.CachedAtUtc)
                    .ToArray(),
                StringComparer.OrdinalIgnoreCase);

        lock (_cacheLock)
        {
            _cachedIndex = index;
            _cachedAtUtc = timeProvider.GetUtcNow();
        }

        return index;
    }

    /// <summary>
    /// Invalidates the cached index so the next call rebuilds it fresh.
    /// </summary>
    public void Invalidate()
    {
        lock (_cacheLock)
        {
            _cachedIndex = null;
        }
    }
}
