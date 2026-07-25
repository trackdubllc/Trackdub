using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedTtsEngine(IRuntimePlanner runtimePlanner,
    IEnumerable<ITtsEngineAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : ITtsEngine, ITtsEngineWithExecutionSummary, IStageRuntimeExecutionReporter
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

        // Derive the execution summary structurally from the plan we just routed to. The plan
        // already carries the authoritative runtime attributes (provider, model id/alias,
        // variant), so we don't need to read the adapter's mutable LastExecutionSummary —
        // which is shared across parallel synthesis tasks and would race. This makes the
        // returned summary deterministic for any homogeneous batch (same plan).
        StageRuntimeExecutionSummary summary = BuildSummaryFromPlan(plan, options);

        lock (summarySync)
        {
            lastExecutionSummary = summary;
        }

        return (result, summary);
    }

    private static StageRuntimeExecutionSummary BuildSummaryFromPlan(
        StageRuntimePlan plan,
        InferenceRequestOptions options)
    {
        string requestedProvider = !string.IsNullOrWhiteSpace(options.PreferredExecutionProvider)
            ? options.PreferredExecutionProvider!
            : plan.ExecutionProvider?.ToString() ?? "default";
        string selectedProvider = plan.ExecutionProvider?.ToString() ?? "default";
        return new StageRuntimeExecutionSummary(
            RequestedProvider: requestedProvider,
            SelectedProvider: selectedProvider,
            ModelId: plan.ModelId,
            ModelAlias: plan.ModelAlias,
            ModelVariant: plan.Variant,
            BootstrapDetail: plan.Fallback?.Detail);
    }

    private async Task<(ITtsEngineAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        TtsSynthesisRequest request,
        InferenceRequestOptions options,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Tts,
                options.NormalizedPreferredModelAlias,
                SourceLanguage: request.LanguageCode,
                RequirePreferredModelAlias: options.RequirePreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);

        ITtsEngineAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.Tts, plan, adapters);
        return (adapter, plan);
    }
}
