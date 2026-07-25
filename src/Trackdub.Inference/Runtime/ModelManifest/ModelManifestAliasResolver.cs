using Trackdub.Contracts;

namespace Trackdub.Inference.Runtime.ModelManifest;

public sealed class ModelManifestAliasResolver(BundledModelManifestRegistry registry) : IModelAliasResolver
{
    private readonly BundledModelManifestRegistry registry = registry ?? throw new ArgumentNullException(nameof(registry));

    public bool TryResolveModelId(string modelAlias, out string? modelId)
    {
        modelId = null;
        if (string.IsNullOrWhiteSpace(modelAlias))
        {
            return false;
        }

        if (!registry.TryResolve(modelAlias, out BundledModelManifestResolution? resolution) || resolution is null)
        {
            return false;
        }

        modelId = resolution.Entry.ModelId;
        return true;
    }
}
