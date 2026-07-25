namespace Trackdub.Composition.StarterPacks;

internal static class StarterPackShippingIds
{
    internal static readonly string[] All = ["basic", "balanced", "premium", "cloud"];

    internal static bool IsShippingPack(string packId) =>
        All.Contains(packId, StringComparer.OrdinalIgnoreCase);
}
