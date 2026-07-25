using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Runtime.Routing;

namespace Trackdub.Inference.Onnx.Translation;

public sealed class RoutedTranslationEngine(
    ITranslationLanguageRouter translationLanguageRouter,
    IEnumerable<ITranslationEngineAdapter> adapters)
    : ITranslationEngine, IStageRuntimeExecutionReporter, ITranslationExecutionMetadataReporter
{
    private readonly ITranslationLanguageRouter translationLanguageRouter = translationLanguageRouter ?? throw new ArgumentNullException(nameof(translationLanguageRouter));
    private readonly IReadOnlyList<ITranslationEngineAdapter> adapters = (adapters ?? throw new ArgumentNullException(nameof(adapters))).ToArray();

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public TranslationExecutionMetadata? LastExecutionMetadata { get; private set; }

    public async Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        TranslationRouteSelection route = await translationLanguageRouter.ResolveRouteAsync(
            request.SourceLanguage,
            request.TargetLanguage,
            cancellationToken,
            request.PreferredModelAlias).ConfigureAwait(false);
        if (!route.IsAvailable)
        {
            throw new InvalidOperationException(
                route.UnavailableReason ??
                $"Translation route {request.SourceLanguage} -> {request.TargetLanguage} is not available.");
        }

        TranslationRequest routedRequest = request with
        {
            PreferredModelAlias = route.PreferredModelAlias,
            ResolvedModelEntryPath = route.ResolvedModelEntryPath
        };

        ITranslationEngineAdapter selectedEngine = SelectAdapter(route);

        IReadOnlyList<TranslatedTextSegment> translatedSegments = await selectedEngine.TranslateAsync(
            routedRequest,
            cancellationToken).ConfigureAwait(false);

        LastExecutionSummary = selectedEngine is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;
        LastExecutionMetadata = new TranslationExecutionMetadata(
            route.ProviderName,
            LastExecutionSummary?.ModelId ?? route.ModelId,
            LastExecutionSummary?.ModelAlias ?? route.PreferredModelAlias,
            LastExecutionSummary?.SelectedProvider,
            route.RoutingKind);
        return translatedSegments;
    }

    private ITranslationEngineAdapter SelectAdapter(TranslationRouteSelection route)
    {
        if (adapters.Count == 0)
        {
            throw new InvalidOperationException("No translation inference adapters are registered.");
        }

        string? engineFamily = InferenceEngineAdapterSelector.NormalizeEngineFamily(route.EngineFamily);
        if (engineFamily is null)
        {
            throw new InvalidOperationException(
                $"Translation route {route.SourceLanguage} -> {route.TargetLanguage} did not specify an engine family.");
        }

        ITranslationEngineAdapter? adapter = adapters.FirstOrDefault(candidate =>
            string.Equals(candidate.EngineFamily, engineFamily, StringComparison.OrdinalIgnoreCase));
        if (adapter is null)
        {
            throw new InvalidOperationException(
                $"No translation inference adapter is registered for engine family '{engineFamily}'.");
        }

        return adapter;
    }
}
