using Trackdub.Application.Runtime;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;

namespace Trackdub.Composition.StarterPacks;

internal static class StarterPackHardwareResolver
{
    public static async Task<StarterPackHardwareProfile> ResolveHardwareProfileAsync(
        IHardwareProfilerService hardwareProfilerService,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(hardwareProfilerService);

        HardwareProfilerViewState viewState = await hardwareProfilerService
            .GetViewStateAsync(cancellationToken)
            .ConfigureAwait(false);

        HardwareQualityPreset preset = viewState.EffectiveRecommendation?.Preset ?? viewState.EffectivePreset;

        return StarterPackStageMapping.FromHardwareQualityPreset(preset);
    }

    public static string? ResolveVariantAlias(
        StarterPackDefinition pack,
        string modelId,
        StarterPackHardwareProfile hardwareProfile)
    {
        ArgumentNullException.ThrowIfNull(pack);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        string hardwareKey = StarterPackStageMapping.ToHardwareProfileKey(hardwareProfile);
        StarterPackModelDefinition? model = StarterPackResolver.FindModelDefinition(pack, modelId);
        if (model is null ||
            !model.RuntimeDefaults.TryGetValue(hardwareKey, out StarterPackRuntimeDefaults? runtimeDefaults))
        {
            return null;
        }

        return string.IsNullOrWhiteSpace(runtimeDefaults.Variant) ? null : runtimeDefaults.Variant.Trim();
    }
}
