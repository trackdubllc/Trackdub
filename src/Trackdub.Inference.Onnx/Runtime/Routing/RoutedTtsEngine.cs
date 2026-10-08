using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedTtsEngine(IRuntimePlanner runtimePlanner,
    IEnumerable<ITtsEngineAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IDeviceEnumerator? deviceEnumerator = null)
    : ITtsEngine, ITtsEngineWithExecutionSummary, IStageRuntimeExecutionReporter, ITtsAcceleratorPlacementResolver
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IReadOnlyList<ITtsEngineAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();
    private readonly object summarySync = new();
    private StageRuntimeExecutionSummary? lastExecutionSummary;

    /// <summary>
    /// Last-write-wins cache of the most recent synthesis summary. Retained for the
    /// <see cref="IStageRuntimeExecutionReporter"/> contract used by
    /// <c>StageRunHelper.ApplyRuntimeExecutionSummaryAsync</c> on single-call (sequential)
    /// scenarios. Parallel callers must use <see cref="SynthesizeWithSummaryAsync"/> and
    /// aggregate summaries explicitly to avoid relying on this mutable state under races.
    /// </summary>
    public StageRuntimeExecutionSummary? LastExecutionSummary
    {
        get
        {
            lock (summarySync)
            {
                return lastExecutionSummary;
            }
        }
    }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        (TtsSynthesisResult result, _) = await SynthesizeWithSummaryAsync(request, cancellationToken)
            .ConfigureAwait(false);
        return result;
    }

    public async Task<(TtsSynthesisResult Result, StageRuntimeExecutionSummary? Summary)> SynthesizeWithSummaryAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;
        (ITtsEngineAdapter adapter, StageRuntimePlan plan) = await SelectAdapterAsync(
            request,
            options,
            cancellationToken).ConfigureAwait(false);

        TtsSynthesisResult result = await adapter.SynthesizeAsync(request, plan, cancellationToken).ConfigureAwait(false);

        // The plan describes the requested route, while the result records the provider
        // actually used by this synthesis call (including runtime fallback). Do not read
        // the adapter's mutable LastExecutionSummary: parallel calls could overwrite it.
        StageRuntimeExecutionSummary summary = BuildSummary(plan, options, result);

        lock (summarySync)
        {
            lastExecutionSummary = summary;
        }

        return (result, summary);
    }

    private static StageRuntimeExecutionSummary BuildSummary(
        StageRuntimePlan plan,
        InferenceRequestOptions options,
        TtsSynthesisResult result)
    {
        string requestedProvider = !string.IsNullOrWhiteSpace(options.PreferredExecutionProvider)
            ? options.PreferredExecutionProvider!
            : plan.ExecutionProvider?.ToString() ?? "default";
        string selectedProvider = string.IsNullOrWhiteSpace(result.Provider)
            ? "unknown"
            : result.Provider;
        return new StageRuntimeExecutionSummary(
            RequestedProvider: requestedProvider,
            SelectedProvider: selectedProvider,
            ModelId: plan.ModelId,
            ModelAlias: plan.ModelAlias,
            ModelVariant: plan.Variant,
            BootstrapDetail: plan.Fallback?.Detail);
    }

    public async Task<TtsAcceleratorPlacement?> ResolvePlacementAsync(
        InferenceRequestOptions options,
        string? languageCode,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        StageRuntimePlan plan = await PlanAsync(options, languageCode, cancellationToken).ConfigureAwait(false);
        if (plan.ExecutionProvider is not ExecutionProviderKind provider)
        {
            return null;
        }

        if (provider is ExecutionProviderKind.Cpu or ExecutionProviderKind.Dnnl)
        {
            return new TtsAcceleratorPlacement(AcceleratorRouted: false, DeviceVramMb: null);
        }

        IReadOnlyList<DeviceEntry> devices = deviceEnumerator is null
            ? []
            : await deviceEnumerator.GetDevicesAsync(cancellationToken).ConfigureAwait(false);
        return new TtsAcceleratorPlacement(
            AcceleratorRouted: true,
            DeviceVramMb: ResolvePlannedDeviceVramMb(plan.DeviceIndex, devices));
    }

    /// <summary>
    /// Dedicated memory of the planned device. When the plan names no device, assumes the
    /// smallest GPU so the bound never exceeds what the executing device allows. Null when the
    /// planned device reports no dedicated memory or no GPU reading exists.
    /// </summary>
    internal static long? ResolvePlannedDeviceVramMb(int? deviceIndex, IReadOnlyList<DeviceEntry> devices)
    {
        if (deviceIndex is int index &&
            devices.FirstOrDefault(device => device.DeviceIndex == index) is DeviceEntry planned)
        {
            return planned.DedicatedVramMb > 0 ? planned.DedicatedVramMb : null;
        }

        int[] gpuVramMb = devices
            .Where(static device => device.Kind is DeviceKind.DiscreteGpu or DeviceKind.IntegratedGpu &&
                                    device.DedicatedVramMb > 0)
            .Select(static device => device.DedicatedVramMb)
            .ToArray();
        return gpuVramMb.Length > 0 ? gpuVramMb.Min() : null;
    }

    private async Task<(ITtsEngineAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        TtsSynthesisRequest request,
        InferenceRequestOptions options,
        CancellationToken cancellationToken)
    {
        StageRuntimePlan plan = await PlanAsync(options, request.LanguageCode, cancellationToken).ConfigureAwait(false);

        ITtsEngineAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.Tts, plan, adapters);
        return (adapter, plan);
    }

    // Shared by synthesis and placement so the concurrency bound reads the same plan synthesis runs.
    private async Task<StageRuntimePlan> PlanAsync(
        InferenceRequestOptions options,
        string? languageCode,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Tts,
                options.NormalizedPreferredModelAlias,
                SourceLanguage: languageCode,
                RequirePreferredModelAlias: options.RequirePreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        return await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);
    }
}
