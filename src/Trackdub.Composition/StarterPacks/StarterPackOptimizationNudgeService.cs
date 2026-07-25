using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackOptimizationNudgeService(
    IStarterPackCatalog catalog,
    IModelInventoryService inventoryService) : IStarterPackOptimizationNudgeService
{
    public async Task<IReadOnlyList<StarterPackOptimizationNudge>> GetNudgesAsync(
        string packId,
        string profileId,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        StarterPackDefinition pack = await catalog.GetAsync(packId, cancellationToken).ConfigureAwait(false);
        if (pack.PackKind == StarterPackKind.Cloud)
        {
            return [];
        }

        StarterPackProfileDefinition profile = StarterPackResolver.ResolveProfile(pack, profileId);
        IReadOnlyList<ModelInventoryEntry> inventory = await inventoryService
            .GetAllAsync(cancellationToken)
            .ConfigureAwait(false);

        var nudges = new List<StarterPackOptimizationNudge>();
        foreach (StarterPackModelDefinition model in StarterPackResolver.GetActiveModelsForProfile(pack, profile.Id))
        {
            if (string.Equals(model.Source, "cloud", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!ShouldNudgeVariantPreference(model.VariantPreference))
            {
                continue;
            }

            ModelInventoryEntry? entry = inventory.FirstOrDefault(candidate =>
                string.Equals(candidate.ModelId, model.ModelId, StringComparison.OrdinalIgnoreCase));
            if (entry is null ||
                !entry.IsOliveOptimizable ||
                entry.State is not (ModelCacheState.Ready or ModelCacheState.Installed))
            {
                continue;
            }

            if (HasInstalledOptimizedVariant(entry))
            {
                continue;
            }

            nudges.Add(new StarterPackOptimizationNudge(model.ModelId, model.Alias));
        }

        return nudges;
    }

    private static bool ShouldNudgeVariantPreference(string variantPreference)
    {
        if (string.IsNullOrWhiteSpace(variantPreference) ||
            string.Equals(variantPreference, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return string.Equals(variantPreference, "optimized", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasInstalledOptimizedVariant(ModelInventoryEntry entry) =>
        entry.OptimizedVariants.Any(variant =>
            variant.State is ModelCacheState.Ready or ModelCacheState.Installed);
}
