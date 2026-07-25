using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackPatchApplier : IStarterPackPatchApplier
{
    public StarterPackPatchResult Apply(
        StarterPackDefinition pack,
        IReadOnlyList<StarterPackPatchOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(operations);

        StarterPackDefinition current = pack;
        var applied = new List<StarterPackPatchOperation>();
        var rejected = new List<StarterPackPatchOperation>();

        foreach (StarterPackPatchOperation operation in operations)
        {
            StarterPackDefinition? next = operation.Kind switch
            {
                StarterPackPatchKind.SetStageExecutionProvider => TrySetStageExecutionProvider(current, operation),
                StarterPackPatchKind.SwapStageModelAlias => TrySwapStageModelAlias(current, operation),
                StarterPackPatchKind.SetOptionalModelEnabled => TrySetOptionalModelEnabled(current, operation),
                StarterPackPatchKind.FlagNotRunnable => TryFlagNotRunnable(current, operation),
                _ => null
            };

            if (next is null)
            {
                rejected.Add(operation);
                continue;
            }

            current = next;
            applied.Add(operation);
        }

        return new StarterPackPatchResult(current, applied, rejected, applied.Count > 0);
    }

    private static StarterPackDefinition? TrySetStageExecutionProvider(
        StarterPackDefinition pack,
        StarterPackPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Stage) || string.IsNullOrWhiteSpace(operation.Value))
        {
            return null;
        }

        bool matched = false;
        var updatedModels = new List<StarterPackModelDefinition>(pack.Models.Count);
        foreach (StarterPackModelDefinition model in pack.Models)
        {
            if (!string.Equals(model.Stage, operation.Stage, StringComparison.OrdinalIgnoreCase))
            {
                updatedModels.Add(model);
                continue;
            }

            matched = true;
            var updatedDefaults = model.RuntimeDefaults.ToDictionary(
                pair => pair.Key,
                pair => pair.Value with { ExecutionProvider = operation.Value });
            updatedModels.Add(model with { RuntimeDefaults = updatedDefaults });
        }

        return matched ? pack with { Models = updatedModels } : null;
    }

    private static StarterPackDefinition? TrySwapStageModelAlias(
        StarterPackDefinition pack,
        StarterPackPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Stage) || string.IsNullOrWhiteSpace(operation.Value))
        {
            return null;
        }

        bool matched = false;
        var updatedModels = new List<StarterPackModelDefinition>(pack.Models.Count);
        foreach (StarterPackModelDefinition model in pack.Models)
        {
            if (!matched && string.Equals(model.Stage, operation.Stage, StringComparison.OrdinalIgnoreCase))
            {
                matched = true;
                updatedModels.Add(model with { Alias = operation.Value });
                continue;
            }

            updatedModels.Add(model);
        }

        return matched ? pack with { Models = updatedModels } : null;
    }

    private static StarterPackDefinition? TrySetOptionalModelEnabled(
        StarterPackDefinition pack,
        StarterPackPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Value))
        {
            return null;
        }

        var optionalModels = new List<string>(pack.OptionalModels ?? []);
        if (!optionalModels.Contains(operation.Value, StringComparer.OrdinalIgnoreCase))
        {
            optionalModels.Add(operation.Value);
        }

        return pack with { OptionalModels = optionalModels };
    }

    private static StarterPackDefinition? TryFlagNotRunnable(
        StarterPackDefinition pack,
        StarterPackPatchOperation operation)
    {
        if (string.IsNullOrWhiteSpace(operation.Stage))
        {
            return null;
        }

        bool matched = pack.Models.Any(model =>
            string.Equals(model.Stage, operation.Stage, StringComparison.OrdinalIgnoreCase));

        return matched ? pack : null;
    }
}
