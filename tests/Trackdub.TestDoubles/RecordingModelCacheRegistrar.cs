using Trackdub.Contracts.Licensing;
using Trackdub.Domain;

namespace Trackdub.TestDoubles;

/// <summary>
/// Records the last cache registration and exposes it for preflight lookup in tests.
/// </summary>
public sealed class RecordingModelCacheRegistrar : IModelCacheRegistrar, IModelCacheRecordLookup
{
    public LocalModelCacheRecord? Record { get; private set; }

    public Task RegisterAsync(
        LocalModelCacheRecord record,
        CancellationToken cancellationToken = default)
    {
        Record = record;
        return Task.CompletedTask;
    }

    public LocalModelCacheRecord? Find(string modelId, string rootPath)
    {
        if (Record is null)
        {
            return null;
        }

        if (!string.Equals(Record.ModelId, modelId, StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (!string.Equals(Path.GetFullPath(Record.RootPath), Path.GetFullPath(rootPath), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        return Record.IntegrityFailed ? null : Record;
    }
}
