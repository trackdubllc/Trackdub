using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.StarterPacks;

public sealed class StarterPackCompatibilityService(
    IStarterPackCatalog catalog,
    IHardwareProfilerService hardwareProfiler,
    IRuntimePlanner runtimePlanner,
    BundledModelManifestRegistry? manifestRegistry = null) : IStarterPackCompatibilityService
{
    public async Task<StarterPackCompatibilityReport> EvaluateAsync(
        string packId,
        string profileId,
        StarterPackHardwareProfile? hardwareProfile = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(packId);
        ArgumentException.ThrowIfNullOrWhiteSpace(profileId);

        StarterPackDefinition pack = await catalog.GetAsync(packId, cancellationToken).ConfigureAwait(false);
        StarterPackProfileDefinition profile = StarterPackResolver.ResolveProfile(pack, profileId);
        HardwareProfilerViewState viewState = await hardwareProfiler
            .GetViewStateAsync(cancellationToken)
            .ConfigureAwait(false);
        StarterPackHardwareProfile resolvedHardwareProfile = hardwareProfile ?? ResolveHardwareProfile(viewState);
        string hardwareKey = StarterPackStageMapping.ToHardwareProfileKey(resolvedHardwareProfile);

        IReadOnlyList<StarterPackModelDefinition> activeModels =
            StarterPackResolver.GetActiveModelsForProfile(pack, profile.Id);
        VramFilterResult? vramFilter = TryFilterByVram(activeModels, viewState);
        IReadOnlySet<string> partialOffloadModelIds = vramFilter is null
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : vramFilter.PartialOffload.Select(model => model.ModelId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (vramFilter is not null)
        {
            activeModels = vramFilter.Optimal
                .Concat(vramFilter.PartialOffload)
                .ToList();
        }

        var stages = new List<StageCompatibilityEntry>();
        foreach (StarterPackModelDefinition model in activeModels)
        {
            StageCompatibilityEntry stage = await EvaluateModelStageAsync(
                model,
                hardwareKey,
                cancellationToken).ConfigureAwait(false);
            if (stage.Runnable && partialOffloadModelIds.Contains(model.ModelId))
            {
                stage = stage with
                {
                    FallbackApplied = true,
                    FallbackReason = "partial_offload_required"
                };
            }

            stages.Add(stage);
        }

        if (vramFilter is not null)
        {
            foreach (StarterPackModelDefinition model in vramFilter.HardExcluded)
            {
                stages.Add(CreateInsufficientVramStage(model, hardwareKey));
            }
        }

        bool allRunnable = stages.Count == 0 || stages.All(stage => stage.Runnable);
        bool anyFallback = stages.Any(stage => stage.FallbackApplied);

        return new StarterPackCompatibilityReport(
            packId,
            profileId,
            hardwareKey,
            stages,
            allRunnable,
            anyFallback);
    }

    private async Task<StageCompatibilityEntry> EvaluateModelStageAsync(
        StarterPackModelDefinition model,
        string hardwareKey,
        CancellationToken cancellationToken)
    {
        string stageName = StarterPackStageMapping.ToStageName(model.Stage);
        StarterPackRuntimeDefaults requestedDefaults = ResolveRuntimeDefaults(model, hardwareKey);

        string requestedVariant = requestedDefaults.Variant;
        string requestedEp = requestedDefaults.ExecutionProvider;

        if (!TryBuildPlanningRequest(
                stageName,
                model.ModelId,
                requestedVariant,
                requestedEp,
                out StageRuntimePlanningRequest? planningRequest,
                out string? blockedReason))
        {
            return new StageCompatibilityEntry(
                stageName,
                model.Alias,
                requestedVariant,
                requestedEp,
                requestedVariant,
                requestedEp,
                FallbackApplied: false,
                FallbackReason: blockedReason,
                Runnable: false);
        }

        StageRuntimePlan plan = await runtimePlanner
            .PlanAsync(planningRequest, cancellationToken)
            .ConfigureAwait(false);

        string resolvedVariant = plan.Variant ?? requestedVariant;
        string resolvedEp = plan.ExecutionProvider is ExecutionProviderKind selectedProvider
            ? RuntimeProviderTokenCompatibility.ToManifestToken(selectedProvider)
            : requestedEp;
        bool fallbackApplied =
            plan.Fallback is not null ||
            !string.Equals(resolvedVariant, requestedVariant, StringComparison.OrdinalIgnoreCase) ||
            (!IsAutoExecutionProvider(requestedEp) && !ExecutionProviderTokensMatch(resolvedEp, requestedEp));
        string? fallbackReason = ResolveFallbackReason(plan);
        bool runnable = plan.IsRunnable();

        return new StageCompatibilityEntry(
            stageName,
            model.Alias,
            requestedVariant,
            requestedEp,
            resolvedVariant,
            resolvedEp,
            fallbackApplied,
            fallbackReason,
            runnable)
        {
            Warnings = plan.Warnings
                .Select(warning => warning.Detail is null
                    ? warning.Code.ToString()
                    : $"{warning.Code}: {warning.Detail}")
                .ToArray()
        };
    }

    private static bool TryBuildPlanningRequest(
        string stageName,
        string modelId,
        string requestedVariant,
        string requestedEp,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out StageRuntimePlanningRequest? request,
        out string? blockedReason)
    {
        request = null;
        blockedReason = null;
        RuntimeStage runtimeStage = MapRuntimeStage(stageName);
        string? preferredVariant = string.IsNullOrWhiteSpace(requestedVariant) ||
            requestedVariant.Equals("default", StringComparison.OrdinalIgnoreCase)
                ? requestedVariant
                : requestedVariant.Trim();

        if (string.IsNullOrWhiteSpace(requestedEp) ||
            requestedEp.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            request = new StageRuntimePlanningRequest(
                runtimeStage,
                PreferredModelAlias: modelId,
                RequirePreferredModelAlias: true,
                PreferredModelVariantAlias: preferredVariant);
            return true;
        }

        if (!RuntimeProviderTokenCompatibility.TryParseProviderToken(requestedEp, out ExecutionProviderKind provider))
        {
            blockedReason = $"unknown_execution_provider:{requestedEp}";
            return false;
        }

        request = new StageRuntimePlanningRequest(
            runtimeStage,
            PreferredModelAlias: modelId,
            RequirePreferredModelAlias: true,
            PreferredExecutionProvider: provider,
            RequirePreferredExecutionProvider: true,
            PreferredModelVariantAlias: preferredVariant);
        return true;
    }

    private static string? ResolveFallbackReason(StageRuntimePlan plan)
    {
        if (plan.Status == StageRuntimePlanStatus.DownloadRequired)
        {
            return "download_required";
        }

        if (plan.Status == StageRuntimePlanStatus.Blocked)
        {
            return plan.Fallback is null
                ? "blocked"
                : $"blocked:{ToReasonToken(plan.Fallback.Code.ToString())}";
        }

        if (plan.Fallback is not null)
        {
            return ToReasonToken(plan.Fallback.Code.ToString());
        }

        return null;
    }

    private static string ToReasonToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var builder = new System.Text.StringBuilder(value.Length + 8);
        for (int index = 0; index < value.Length; index++)
        {
            char c = value[index];
            if (char.IsUpper(c) && index > 0)
            {
                builder.Append('_');
            }

            builder.Append(char.ToLowerInvariant(c));
        }

        return builder.ToString();
    }

    private static bool ExecutionProviderTokensMatch(string resolvedEp, string requestedEp)
    {
        if (string.Equals(resolvedEp, requestedEp, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return RuntimeProviderTokenCompatibility.TryParseProviderToken(resolvedEp, out ExecutionProviderKind resolvedProvider) &&
            RuntimeProviderTokenCompatibility.TryParseProviderToken(requestedEp, out ExecutionProviderKind requestedProvider) &&
            resolvedProvider == requestedProvider;
    }

    private static bool IsAutoExecutionProvider(string executionProvider) =>
        string.IsNullOrWhiteSpace(executionProvider) ||
        executionProvider.Equals("auto", StringComparison.OrdinalIgnoreCase);

    private static StarterPackHardwareProfile ResolveHardwareProfile(HardwareProfilerViewState viewState)
    {
        HardwareQualityPreset preset = viewState.EffectiveRecommendation?.Preset ?? viewState.EffectivePreset;
        return StarterPackStageMapping.FromHardwareQualityPreset(preset);
    }

    private VramFilterResult? TryFilterByVram(
        IReadOnlyList<StarterPackModelDefinition> activeModels,
        HardwareProfilerViewState viewState)
    {
        if (manifestRegistry is null ||
            viewState.Snapshot?.Fingerprint.GpuDedicatedMemoryBytes is not { } dedicatedVramBytes ||
            dedicatedVramBytes <= 0)
        {
            return null;
        }

        int availableVramMb = (int)Math.Min(dedicatedVramBytes / (1024 * 1024), int.MaxValue);
        Dictionary<string, BundledModelManifestEntry> manifestLookup = manifestRegistry.Entries
            .ToDictionary(entry => entry.ModelId, StringComparer.OrdinalIgnoreCase);
        return StarterPackResolver.FilterByVram(activeModels, manifestLookup, availableVramMb);
    }

    private static StageCompatibilityEntry CreateInsufficientVramStage(
        StarterPackModelDefinition model,
        string hardwareKey)
    {
        string stageName = StarterPackStageMapping.ToStageName(model.Stage);
        StarterPackRuntimeDefaults requestedDefaults = ResolveRuntimeDefaults(model, hardwareKey);
        return new StageCompatibilityEntry(
            stageName,
            model.Alias,
            requestedDefaults.Variant,
            requestedDefaults.ExecutionProvider,
            requestedDefaults.Variant,
            requestedDefaults.ExecutionProvider,
            FallbackApplied: false,
            FallbackReason: "insufficient_vram",
            Runnable: false);
    }

    private static StarterPackRuntimeDefaults ResolveRuntimeDefaults(
        StarterPackModelDefinition model,
        string hardwareKey)
    {
        if (model.RuntimeDefaults.TryGetValue(hardwareKey, out StarterPackRuntimeDefaults? requestedDefaults))
        {
            return requestedDefaults;
        }

        string nearestKey = ResolveNearestHardwareKey(model.RuntimeDefaults, hardwareKey);
        return model.RuntimeDefaults[nearestKey];
    }

    private static string ResolveNearestHardwareKey(
        IReadOnlyDictionary<string, StarterPackRuntimeDefaults> runtimeDefaults,
        string hardwareKey)
    {
        string[] order = ["turbo_gpu", "balanced_gpu", "cpu_safe"];
        int requestedIndex = Array.FindIndex(order, key => string.Equals(key, hardwareKey, StringComparison.OrdinalIgnoreCase));
        if (requestedIndex < 0)
        {
            requestedIndex = order.Length - 1;
        }

        for (int index = requestedIndex; index < order.Length; index++)
        {
            if (runtimeDefaults.ContainsKey(order[index]))
            {
                return order[index];
            }
        }

        return runtimeDefaults.Keys.First();
    }

    private static RuntimeStage MapRuntimeStage(string stageName) =>
        stageName switch
        {
            StageNames.Vad => RuntimeStage.Vad,
            StageNames.Asr => RuntimeStage.Asr,
            StageNames.Translation => RuntimeStage.Translation,
            StageNames.Tts => RuntimeStage.Tts,
            StageNames.Diarization => RuntimeStage.Diarization,
            StageNames.Separation => RuntimeStage.Separation,
            _ => RuntimeStage.Asr
        };
}
