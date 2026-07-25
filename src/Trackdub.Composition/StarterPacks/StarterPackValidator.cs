using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackValidator
{
    public void Validate(StarterPackDefinition pack, BundledModelManifestRegistry manifestRegistry)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentNullException.ThrowIfNull(manifestRegistry);

        if (pack.SchemaVersion != 1)
        {
            throw new InvalidOperationException($"Starter pack '{pack.Id}' has unsupported schema_version {pack.SchemaVersion}.");
        }

        if (pack.Profiles.Count == 0)
        {
            throw new InvalidOperationException($"Starter pack '{pack.Id}' must define at least one profile.");
        }

        if (pack.PackKind == StarterPackKind.Cloud)
        {
            if (pack.CloudDefaults is null)
            {
                throw new InvalidOperationException($"Starter pack '{pack.Id}' requires cloud_defaults.");
            }

            StarterPackCloudDefaultsMapper.Validate(pack.CloudDefaults);

            if (pack.Models.Count > 0)
            {
                throw new InvalidOperationException($"Starter pack '{pack.Id}' must not define local models.");
            }

            foreach (StarterPackProfileDefinition profile in pack.Profiles)
            {
                _ = StarterPackResolver.ResolveProfile(pack, profile.Id);
                if (!string.IsNullOrWhiteSpace(profile.AsrModelId))
                {
                    throw new InvalidOperationException(
                        $"Starter pack '{pack.Id}' profile '{profile.Id}' must not specify asr_model_id.");
                }
            }

            return;
        }

        if (pack.Apply is not null)
        {
            StarterPackDataDrivenApplyMapper.Validate(pack.Apply);
        }

        if (pack.PackKind == StarterPackKind.Hybrid && pack.CloudDefaults is not null)
        {
            StarterPackCloudDefaultsMapper.Validate(pack.CloudDefaults);
        }

        foreach (StarterPackProfileDefinition profile in pack.Profiles)
        {
            _ = StarterPackResolver.ResolveProfile(pack, profile.Id);
            if (pack.PackOrigin == StarterPackOrigin.User && pack.Apply is not null)
            {
                continue;
            }

            string? asrModelId = StarterPackResolver.ResolveProfileAsrModelId(pack, profile);
            if (string.IsNullOrWhiteSpace(asrModelId))
            {
                throw new InvalidOperationException($"Starter pack '{pack.Id}' profile '{profile.Id}' has no ASR model.");
            }

            BundledModelManifestEntry? asrEntry = manifestRegistry.Entries
                .FirstOrDefault(entry => entry.ModelId.Equals(asrModelId, StringComparison.OrdinalIgnoreCase));
            if (asrEntry is null)
            {
                throw new InvalidOperationException($"Starter pack '{pack.Id}' references unknown ASR model '{asrModelId}'.");
            }
        }

        foreach (StarterPackModelDefinition model in pack.Models)
        {
            BundledModelManifestEntry? entry = manifestRegistry.Entries
                .FirstOrDefault(candidate => candidate.ModelId.Equals(model.ModelId, StringComparison.OrdinalIgnoreCase));
            if (entry is null)
            {
                throw new InvalidOperationException($"Starter pack '{pack.Id}' references unknown model '{model.ModelId}'.");
            }

            if (!entry.CommercialAllowed || entry.Lane == ModelLane.Experimental)
            {
                throw new InvalidOperationException($"Starter pack '{pack.Id}' model '{model.ModelId}' is not commercial-safe.");
            }

            _ = StarterPackStageMapping.ToStageName(model.Stage);

            foreach ((string hardwareProfile, StarterPackRuntimeDefaults defaults) in model.RuntimeDefaults)
            {
                if (!RuntimeProviderTokenCompatibility.IsKnownProviderToken(defaults.ExecutionProvider, allowAuto: true))
                {
                    throw new InvalidOperationException(
                        $"Starter pack '{pack.Id}' model '{model.ModelId}' has unknown execution_provider '{defaults.ExecutionProvider}'.");
                }

                ValidateVariant(entry, defaults.Variant, defaults.ExecutionProvider, pack.Id, model.ModelId, hardwareProfile);
            }
        }

        if (pack.Translation is not null &&
            !string.Equals(pack.Translation.Strategy, "universal", StringComparison.OrdinalIgnoreCase) &&
            !pack.Id.EndsWith("-pair-addon", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Starter pack '{pack.Id}' must use universal translation strategy unless it is a pair add-on pack.");
        }
    }

    private static void ValidateVariant(
        BundledModelManifestEntry entry,
        string variant,
        string executionProvider,
        string packId,
        string modelId,
        string hardwareProfile)
    {
        if (!TryFindVariant(entry, variant, out BundledModelManifestVariant? manifestVariant))
        {
            throw new InvalidOperationException(
                $"Starter pack '{packId}' model '{modelId}' profile '{hardwareProfile}' references unknown variant '{variant}'.");
        }

        if (manifestVariant is null ||
            string.Equals(executionProvider, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

        if (!RuntimeProviderTokenCompatibility.TryParseProviderToken(executionProvider, out ExecutionProviderKind provider))
        {
            return;
        }

        if (!RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(manifestVariant.SupportedProviders, provider))
        {
            throw new InvalidOperationException(
                $"Starter pack '{packId}' model '{modelId}' profile '{hardwareProfile}' variant '{variant}' does not support execution_provider '{executionProvider}'.");
        }
    }

    private static bool TryFindVariant(
        BundledModelManifestEntry entry,
        string variant,
        out BundledModelManifestVariant? manifestVariant)
    {
        manifestVariant = null;
        if (string.IsNullOrWhiteSpace(variant))
        {
            return true;
        }

        if (string.Equals(variant, "default", StringComparison.OrdinalIgnoreCase))
        {
            manifestVariant = entry.Variants.FirstOrDefault(candidate =>
                candidate.IsDefault || candidate.Alias.Equals("default", StringComparison.OrdinalIgnoreCase));
            return true;
        }

        manifestVariant = entry.Variants.FirstOrDefault(candidate =>
            string.Equals(candidate.Alias, variant, StringComparison.OrdinalIgnoreCase));
        return manifestVariant is not null || entry.Variants.Count == 0;
    }
}
