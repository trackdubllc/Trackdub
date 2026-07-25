using Trackdub.Contracts.StarterPacks;
using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Composition.StarterPacks;

public sealed record VramFilterResult(
    IReadOnlyList<StarterPackModelDefinition> Optimal,
    IReadOnlyList<StarterPackModelDefinition> PartialOffload,
    IReadOnlyList<StarterPackModelDefinition> HardExcluded);

public static class StarterPackResolver
{
    public static StarterPackProfileDefinition ResolveProfile(StarterPackDefinition pack, string profileId)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        string normalizedProfileId = NormalizeLegacyProfileId(pack, profileId);

        StarterPackProfileDefinition? profile = pack.Profiles
            .FirstOrDefault(candidate => string.Equals(candidate.Id, normalizedProfileId, StringComparison.OrdinalIgnoreCase));
        if (profile is null)
        {
            throw new InvalidOperationException($"Starter pack '{pack.Id}' has no profile '{profileId}'.");
        }

        return profile;
    }

    /// <summary>
    /// Maps removed bundled profile ids to the pack default so older applied settings keep working.
    /// </summary>
    public static string NormalizeLegacyProfileId(StarterPackDefinition pack, string profileId)
    {
        if (pack.Profiles.Any(candidate => string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase)))
        {
            return profileId;
        }

        if (string.Equals(pack.Id, "balanced", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profileId, "balanced-multilingual", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDefaultProfileId(pack);
        }

        if (string.Equals(pack.Id, "premium", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(profileId, "premium-multilingual", StringComparison.OrdinalIgnoreCase))
        {
            return ResolveDefaultProfileId(pack);
        }

        return profileId;
    }

    public static string ResolveDefaultProfileId(StarterPackDefinition pack) =>
        pack.Profiles.Count > 0 ? pack.Profiles[0].Id : "default";

    public static IReadOnlyList<string> GetRequiredModelIds(StarterPackDefinition pack, string profileId)
    {
        StarterPackProfileDefinition profile = ResolveProfile(pack, profileId);
        var ids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (StarterPackModelDefinition model in pack.Models)
        {
            if (IsCloudSourcedModel(model))
            {
                continue;
            }

            if (model.Required)
            {
                ids.Add(model.ModelId);
            }
        }

        string? asrModelId = ResolveProfileAsrModelId(pack, profile);
        if (!string.IsNullOrWhiteSpace(asrModelId))
        {
            ids.Add(asrModelId);
        }

        return ids.ToList();
    }

    public static string? ResolveProfileAsrModelId(StarterPackDefinition pack, StarterPackProfileDefinition profile)
    {
        if (!string.IsNullOrWhiteSpace(profile.AsrModelId))
        {
            return profile.AsrModelId;
        }

        if (string.Equals(pack.Id, "basic", StringComparison.OrdinalIgnoreCase))
        {
            return "onnx-community/whisper-tiny";
        }

        return null;
    }

    public static StarterPackModelDefinition? FindModelDefinition(StarterPackDefinition pack, string modelId) =>
        pack.Models.FirstOrDefault(model => string.Equals(model.ModelId, modelId, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Partitions models into three VRAM tiers given the probed available VRAM.
    /// Optimal: model fits fully in GPU memory.
    /// PartialOffload: model supports spillover to system RAM but warns user and nudges Olive optimization.
    /// HardExcluded: VRAM below model minimum — cannot run even with offloading.
    /// Models with no manifest data or zero EstimatedVramMb are placed in Optimal (VRAM-agnostic).
    /// </summary>
    public static VramFilterResult FilterByVram(
        IReadOnlyList<StarterPackModelDefinition> models,
        IReadOnlyDictionary<string, ModelManifest> manifestLookup,
        int availableVramMb) =>
        FilterByVramCore(
            models,
            manifestLookup,
            availableVramMb,
            static manifest => manifest.EstimatedVramMb,
            static manifest => manifest.MinVramMb,
            static manifest => manifest.SupportsPartialOffload);

    public static VramFilterResult FilterByVram(
        IReadOnlyList<StarterPackModelDefinition> models,
        IReadOnlyDictionary<string, BundledModelManifestEntry> manifestLookup,
        int availableVramMb) =>
        FilterByVramCore(
            models,
            manifestLookup,
            availableVramMb,
            static manifest => manifest.EstimatedVramMb,
            static manifest => manifest.MinVramMb,
            static manifest => manifest.SupportsPartialOffload);

    private static VramFilterResult FilterByVramCore<TManifest>(
        IReadOnlyList<StarterPackModelDefinition> models,
        IReadOnlyDictionary<string, TManifest> manifestLookup,
        int availableVramMb,
        Func<TManifest, int> estimatedVramMb,
        Func<TManifest, int> minVramMb,
        Func<TManifest, bool> supportsPartialOffload)
    {
        var optimal = new List<StarterPackModelDefinition>();
        var partialOffload = new List<StarterPackModelDefinition>();
        var hardExcluded = new List<StarterPackModelDefinition>();

        foreach (StarterPackModelDefinition model in models)
        {
            if (!manifestLookup.TryGetValue(model.ModelId, out TManifest? manifest) || estimatedVramMb(manifest) == 0)
            {
                optimal.Add(model);
                continue;
            }

            if (availableVramMb >= estimatedVramMb(manifest))
            {
                optimal.Add(model);
            }
            else if (supportsPartialOffload(manifest) && availableVramMb >= minVramMb(manifest))
            {
                partialOffload.Add(model);
            }
            else
            {
                hardExcluded.Add(model);
            }
        }

        return new VramFilterResult(optimal, partialOffload, hardExcluded);
    }

    public static IReadOnlyList<StarterPackModelDefinition> GetActiveModelsForProfile(
        StarterPackDefinition pack,
        string profileId)
    {
        StarterPackProfileDefinition profile = ResolveProfile(pack, profileId);
        string? asrModelId = ResolveProfileAsrModelId(pack, profile);
        var active = new List<StarterPackModelDefinition>();

        foreach (StarterPackModelDefinition model in pack.Models)
        {
            if (IsCloudSourcedModel(model))
            {
                continue;
            }

            if (model.Required)
            {
                active.Add(model);
                continue;
            }

            if (string.Equals(model.Stage, "asr", StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(asrModelId) &&
                string.Equals(model.ModelId, asrModelId, StringComparison.OrdinalIgnoreCase))
            {
                active.Add(model);
            }
        }

        return active;
    }

    private static bool IsCloudSourcedModel(StarterPackModelDefinition model) =>
        string.Equals(model.Source, "cloud", StringComparison.OrdinalIgnoreCase);
}
