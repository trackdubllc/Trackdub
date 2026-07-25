using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Runtime.Migraphx;

public static class MigraphxProviderOrdering
{
    public static IReadOnlyList<ExecutionProviderKind> ApplyAmdMigraphxFirst(
        IReadOnlyList<ExecutionProviderKind> orderedProviders,
        bool preferMigraphxOnAmdGpu)
    {
        if (!preferMigraphxOnAmdGpu ||
            !orderedProviders.Contains(ExecutionProviderKind.Migraphx) ||
            !orderedProviders.Contains(ExecutionProviderKind.DirectMl))
        {
            return orderedProviders;
        }

        var reordered = orderedProviders
            .Where(provider => provider != ExecutionProviderKind.Migraphx)
            .ToList();
        int directMlIndex = reordered.IndexOf(ExecutionProviderKind.DirectMl);
        reordered.Insert(directMlIndex, ExecutionProviderKind.Migraphx);
        return reordered;
    }

    public static bool ShouldPreferMigraphxOnAmdGpu(HardwareProfile hardwareProfile)
    {
        if (LooksLikeAmdGpu(hardwareProfile.GpuDescription))
        {
            return true;
        }

        return hardwareProfile.Devices?.Any(device =>
            LooksLikeAmdGpu(device.VendorName) || LooksLikeAmdGpu(device.AdapterDescription)) == true;
    }

    internal static bool LooksLikeAmdGpu(string? description) =>
        !string.IsNullOrWhiteSpace(description) &&
        (description.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("Radeon", StringComparison.OrdinalIgnoreCase) ||
         description.Contains("ATI", StringComparison.OrdinalIgnoreCase));
}
