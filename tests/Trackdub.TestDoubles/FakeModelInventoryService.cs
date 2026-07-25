using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.TestDoubles;

public sealed class FakeModelInventoryService(IEnumerable<ModelInventoryEntry>? entries = null) : IModelInventoryService
{
    private readonly List<ModelInventoryEntry> entries = entries?.ToList() ?? [];

    public int GetAllCallCount { get; private set; }

    public void SetEntries(IEnumerable<ModelInventoryEntry> newEntries)
    {
        entries.Clear();
        entries.AddRange(newEntries);
    }

    public Task<IReadOnlyList<ModelInventoryEntry>> GetAllAsync(CancellationToken cancellationToken = default)
    {
        GetAllCallCount++;
        return Task.FromResult<IReadOnlyList<ModelInventoryEntry>>(entries.ToList());
    }

    public Task<ModelInventoryEntry?> GetByModelIdAsync(string modelId, CancellationToken cancellationToken = default)
    {
        ModelInventoryEntry? match = entries.FirstOrDefault(e => e.ModelId == modelId);
        return Task.FromResult(match);
    }
}
