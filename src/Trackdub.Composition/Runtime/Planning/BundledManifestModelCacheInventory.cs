using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Runtime.Planning;

public sealed class BundledManifestModelCacheInventory(BundledModelManifestRegistry manifestRegistry)
    : IModelCacheInventory
{
    private readonly BundledModelManifestRegistry manifestRegistry = manifestRegistry ?? throw new ArgumentNullException(nameof(manifestRegistry));

    public Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default)
    {
        IReadOnlyList<LocalModelCacheRecord> records = manifestRegistry.Entries
            .Where(entry => Directory.Exists(entry.RootDirectory))
            .Select(entry => new LocalModelCacheRecord(
                entry.ModelId,
                entry.RootDirectory,
                entry.Revision,
                entry.Sha256,
                DateTimeOffset.MinValue))
            .ToArray();

        return Task.FromResult(records);
    }
}
