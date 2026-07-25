using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Runtime.Planning;

internal static class GpuVariantPreferencePolicy
{
    private static readonly string[] BlackwellTensorRtRtxPreferredAliases = ["mxfp8", "nvfp4"];

    public static IReadOnlyList<string> GetPreferredGpuVariantAliases(
        StageRuntimeRequirements requirements,
        HardwareProfile hardware,
        ExecutionProviderKind provider,
        BundledModelManifestEntry entry)
    {
        IReadOnlyList<string> baseAliases = requirements.PreferredGpuVariants;
        if (hardware.NvidiaGpuArchitecture != NvidiaGpuArchitectureBucket.Blackwell ||
            provider != ExecutionProviderKind.TensorRTRtx)
        {
            return baseAliases;
        }

        var preferred = new List<string>();
        foreach (string alias in BlackwellTensorRtRtxPreferredAliases)
        {
            if (entry.Variants.Any(variant =>
                    variant.Alias.Equals(alias, StringComparison.OrdinalIgnoreCase) &&
                    RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(variant.SupportedProviders, provider) &&
                    VariantManifestReadiness.HasPinnedDownloadFileHashes(entry, variant)))
            {
                preferred.Add(alias);
            }
        }

        foreach (string alias in baseAliases)
        {
            if (!preferred.Contains(alias, StringComparer.OrdinalIgnoreCase))
            {
                preferred.Add(alias);
            }
        }

        return preferred;
    }
}

internal static class VariantManifestReadiness
{
    private static readonly HashSet<string> HashGatedVariantAliases =
        new(StringComparer.OrdinalIgnoreCase) { "mxfp8", "nvfp4" };

    public static bool IsManifestVariantEligibleForPlanning(
        BundledModelManifestEntry entry,
        BundledModelManifestVariant variant,
        ExecutionProviderKind provider)
    {
        if (!RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(variant.SupportedProviders, provider))
        {
            return false;
        }

        if (!RequiresPinnedDownloadFileHashes(variant))
        {
            return true;
        }

        return HasPinnedDownloadFileHashes(entry, variant);
    }

    public static bool RequiresPinnedDownloadFileHashes(BundledModelManifestVariant variant) =>
        HashGatedVariantAliases.Contains(variant.Alias);

    public static bool HasPinnedDownloadFileHashes(
        BundledModelManifestEntry entry,
        BundledModelManifestVariant variant)
    {
        if (variant.DownloadFiles.Count == 0)
        {
            return true;
        }

        foreach (string relativePath in variant.DownloadFiles)
        {
            if (!entry.DownloadFileHashes.ContainsKey(relativePath))
            {
                return false;
            }
        }

        return true;
    }
}
