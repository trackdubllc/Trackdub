namespace Trackdub.Contracts.StarterPacks;

public interface IStarterPackPatchApplier
{
    StarterPackPatchResult Apply(
        StarterPackDefinition pack,
        IReadOnlyList<StarterPackPatchOperation> operations);
}
