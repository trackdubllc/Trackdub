using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime.Routing;

public sealed class RoutedAudioTranscriptionEngine(IRuntimePlanner runtimePlanner,
    IEnumerable<IAudioTranscriptionEngineAdapter> adapters,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : IAudioTranscriptionEngine, IStageRuntimeExecutionReporter
{
    private readonly IRuntimePlanner runtimePlanner = runtimePlanner ?? throw new ArgumentNullException(nameof(runtimePlanner));
    private readonly IReadOnlyList<IAudioTranscriptionEngineAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken) =>
        TranscribeAsync(
            new AudioTranscriptionRequest(normalizedAudioPath, regions),
            cancellationToken);

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        InferenceRequestOptions options = request.Options ?? InferenceRequestOptions.Default;
        (IAudioTranscriptionEngineAdapter adapter, StageRuntimePlan plan) = await SelectAdapterAsync(
            request,
            options,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<RecognizedTranscriptSegment> segments = await adapter.TranscribeAsync(request, plan, cancellationToken).ConfigureAwait(false);
        LastExecutionSummary = adapter is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        return segments;
    }

    private async Task<(IAudioTranscriptionEngineAdapter Adapter, StageRuntimePlan Plan)> SelectAdapterAsync(
        AudioTranscriptionRequest request,
        InferenceRequestOptions options,
        CancellationToken cancellationToken)
    {
        StageRuntimePlan plan = await runtimePlanner.PlanAsync(
            await StageRuntimePlanningRequestFactory.ApplyPreferredModelTierAsync(new StageRuntimePlanningRequest(
                RuntimeStage.Asr,
                options.NormalizedPreferredModelAlias,
                SourceLanguage: request.SourceLanguage,
                RequirePreferredModelAlias: options.RequirePreferredModelAlias,
                PreferredExecutionProvider: ExecutionProviderRequest.ParsePreferredExecutionProvider(
                    options.PreferredExecutionProvider,
                    options.RequirePreferredExecutionProvider),
                RequirePreferredExecutionProvider: options.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: options.NormalizedPreferredModelVariantAlias),
            runtimePlanningPreferences,
            cancellationToken),
            cancellationToken).ConfigureAwait(false);

        IAudioTranscriptionEngineAdapter adapter = InferenceEngineAdapterSelector.SelectForPlan(RuntimeStage.Asr, plan, adapters);
        return (adapter, plan);
    }
}
