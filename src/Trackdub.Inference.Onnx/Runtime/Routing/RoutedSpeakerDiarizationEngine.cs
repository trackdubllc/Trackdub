using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedSpeakerDiarizationEngine(IRuntimePlanner runtimePlanner,
    IEnumerable<ISpeakerDiarizationEngineAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : ISpeakerDiarizationEngine, IStageRuntimeExecutionReporter
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IReadOnlyList<ISpeakerDiarizationEngineAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        string normalizedAudioPath,
        double durationSeconds,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken) =>
        DiarizeAsync(
            new SpeakerDiarizationRequest(
                normalizedAudioPath,
                durationSeconds,
                speechRegions,
                InferenceRequestOptions.Default),
            cancellationToken);

    public async Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        SpeakerDiarizationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;
        (ISpeakerDiarizationEngineAdapter adapter, StageRuntimePlan plan) = await SelectAdapterAsync(
            options,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<DiarizedSpeakerTurn> turns = await adapter.DiarizeAsync(request, plan, cancellationToken).ConfigureAwait(false);
        LastExecutionSummary = adapter is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        return turns;
    }

    private async Task<(ISpeakerDiarizationEngineAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        InferenceRequestOptions options,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Diarization,
                options.NormalizedPreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);

        ISpeakerDiarizationEngineAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.Diarization, plan, adapters);
        return (adapter, plan);
    }
}
