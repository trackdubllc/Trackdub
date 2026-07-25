using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedStemSeparationEngine(IRuntimePlanner runtimePlanner,
    IEnumerable<IStemSeparationEngineAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : IStemSeparationEngine, IStageRuntimeExecutionReporter
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IReadOnlyList<IStemSeparationEngineAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<StemSeparationResult> SeparateAsync(
        StemSeparationRequest request,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (IStemSeparationEngineAdapter adapter, StageRuntimePlan plan) = await SelectAdapterAsync(
            request,
            cancellationToken).ConfigureAwait(false);

        StemSeparationResult result = await adapter.SeparateAsync(request, plan, progress, cancellationToken).ConfigureAwait(false);
        LastExecutionSummary = adapter is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        return result;
    }

    private async Task<(IStemSeparationEngineAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        StemSeparationRequest request,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Separation,
                request.PreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);

        IStemSeparationEngineAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.Separation, plan, adapters);
        return (adapter, plan);
    }
}
