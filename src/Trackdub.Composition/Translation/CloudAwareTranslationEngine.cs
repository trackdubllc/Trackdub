using System.Runtime.CompilerServices;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Translation;

public sealed class CloudAwareTranslationEngine(
    ITranslationEngine localEngine,
    ITranslationEngine deepLCloudEngine,
    ITranslationEngine openAiCloudEngine,
    ITranslationEngine geminiCloudEngine)
    : IStreamingTranslationEngine, ITranslationExecutionMetadataReporter, IStageRuntimeExecutionReporter
{
    private readonly ITranslationEngine localEngine = localEngine ?? throw new ArgumentNullException(nameof(localEngine));
    private readonly ITranslationEngine deepLCloudEngine = deepLCloudEngine ?? throw new ArgumentNullException(nameof(deepLCloudEngine));
    private readonly ITranslationEngine openAiCloudEngine = openAiCloudEngine ?? throw new ArgumentNullException(nameof(openAiCloudEngine));
    private readonly ITranslationEngine geminiCloudEngine = geminiCloudEngine ?? throw new ArgumentNullException(nameof(geminiCloudEngine));

    // These are updated per TranslateAsync call and are not thread-safe.
    // Safe for sequential pipeline use; revisit if concurrent translation is ever introduced.
    public TranslationExecutionMetadata? LastExecutionMetadata { get; private set; }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ITranslationEngine selectedEngine = SelectEngine(request);

        IReadOnlyList<TranslatedTextSegment> translated = await selectedEngine
            .TranslateAsync(request, cancellationToken)
            .ConfigureAwait(false);

        CaptureExecutionInfo(selectedEngine);
        return translated;
    }

    /// <summary>
    /// Streams when the selected engine supports it; batch-only engines (cloud providers)
    /// execute once and their completed segments are wrapped in input order — compatibility,
    /// not progressive inference.
    /// </summary>
    public async IAsyncEnumerable<PipelineStreamItem<TranslatedTextSegment>> TranslateStreamAsync(
        TranslationRequest request,
        Guid runId,
        string snapshotId,
        Guid sourceRevisionId,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        PipelineStreamItemFactory.ValidateTranslationStreamContext(runId, snapshotId, sourceRevisionId);

        ITranslationEngine selectedEngine = SelectEngine(request);
        try
        {
            if (selectedEngine is IStreamingTranslationEngine streamingEngine)
            {
                await foreach (PipelineStreamItem<TranslatedTextSegment> item in streamingEngine
                    .TranslateStreamAsync(request, runId, snapshotId, sourceRevisionId, cancellationToken)
                    .ConfigureAwait(false))
                {
                    yield return item;
                }
            }
            else
            {
                IReadOnlyList<TranslatedTextSegment> batch = await selectedEngine
                    .TranslateAsync(request, cancellationToken)
                    .ConfigureAwait(false);
                long sequence = 0;
                foreach (TranslatedTextSegment segment in batch.OrderBy(static s => s.Index))
                {
                    yield return PipelineStreamItemFactory.CreateTranslation(
                        segment, runId, snapshotId, sourceRevisionId, sequence++);
                }
            }
        }
        finally
        {
            CaptureExecutionInfo(selectedEngine);
        }
    }

    private ITranslationEngine SelectEngine(TranslationRequest request) =>
        request.PreferredModelAlias switch
        {
            var a when TranslationModelOverrideSettings.IsDeepLModelAlias(a) => deepLCloudEngine,
            var a when TranslationModelOverrideSettings.IsOpenAiGptAlias(a) => openAiCloudEngine,
            var a when TranslationModelOverrideSettings.IsGeminiTranslationAlias(a) => geminiCloudEngine,
            _ => localEngine
        };

    private void CaptureExecutionInfo(ITranslationEngine selectedEngine)
    {
        LastExecutionMetadata = selectedEngine is ITranslationExecutionMetadataReporter metadataReporter
            ? metadataReporter.LastExecutionMetadata
            : null;
        LastExecutionSummary = selectedEngine is IStageRuntimeExecutionReporter runtimeReporter
            ? runtimeReporter.LastExecutionSummary
            : null;
    }
}
