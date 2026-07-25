using Trackdub.Domain;

namespace Trackdub.Contracts;

/// <summary>
/// Queries the current state of all manifest-registered models in the local cache.
/// </summary>
public interface IModelInventoryService
{
    Task<IReadOnlyList<ModelInventoryEntry>> GetAllAsync(CancellationToken cancellationToken = default);

    Task<ModelInventoryEntry?> GetByModelIdAsync(string modelId, CancellationToken cancellationToken = default);
}
