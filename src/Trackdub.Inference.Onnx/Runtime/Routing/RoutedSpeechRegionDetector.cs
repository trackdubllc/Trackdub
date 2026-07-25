using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedSpeechRegionDetector(IRuntimePlanner runtimePlanner,
    IEnumerable<ISpeechRegionDetectorAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : ISpeechRegionDetector, IStageRuntimeExecutionReporter
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IReadOnlyList<ISpeechRegionDetectorAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        string normalizedAudioPath,
        double durationSeconds,
        CancellationToken cancellationToken) =>
        DetectAsync(
            new SpeechRegionDetectionRequest(normalizedAudioPath, durationSeconds),
            cancellationToken);

    public async Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        SpeechRegionDetectionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;
        (ISpeechRegionDetectorAdapter adapter, StageRuntimePlan plan) = await SelectAdapterAsync(
            options,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<SpeechRegion> regions = await adapter.DetectAsync(request, plan, cancellationToken).ConfigureAwait(false);
        LastExecutionSummary = adapter is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        return regions;
    }

    private async Task<(ISpeechRegionDetectorAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        InferenceRequestOptions options,
        CancellationToken cancellationToken)
    {
        StageRuntimePlanningRequest planningRequest = await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(
            new StageRuntimePlanningRequest(
                RuntimeStage.Vad,
                options.NormalizedPreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken).ConfigureAwait(false);

        StageRuntimePlan plan = await runtimePlanner.PlanAsync(planningRequest, cancellationToken).ConfigureAwait(false);

        ISpeechRegionDetectorAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.Vad, plan, adapters);
        return (adapter, plan);
    }
}
