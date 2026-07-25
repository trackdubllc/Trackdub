using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedOverlapRescueEngine(
    IRuntimePlanner runtimePlanner,
    IEnumerable<IOverlapRescueEngineAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : IOverlapRescueEngine, IStageRuntimeExecutionReporter
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IReadOnlyList<IOverlapRescueEngineAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<OverlapRescueResult> RescueAsync(
        OverlapRescueRequest request,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        (IOverlapRescueEngineAdapter adapter, StageRuntimePlan plan) = await SelectAdapterAsync(request, cancellationToken)
            .ConfigureAwait(false);

        OverlapRescueResult result = await adapter.RescueAsync(request, plan, progress, cancellationToken).ConfigureAwait(false);
        LastExecutionSummary = adapter is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        return result;
    }

    private async Task<(IOverlapRescueEngineAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        OverlapRescueRequest request,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.OverlapRescue,
                request.PreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);
        IOverlapRescueEngineAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.OverlapRescue, plan, adapters);
        return (adapter, plan);
    }
}
