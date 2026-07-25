using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Translation;

public sealed class CloudAwareTranslationEngine(
    ITranslationEngine localEngine,
    ITranslationEngine deepLCloudEngine,
    ITranslationEngine openAiCloudEngine,
    ITranslationEngine geminiCloudEngine)
    : ITranslationEngine, ITranslationExecutionMetadataReporter, IStageRuntimeExecutionReporter
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
        ITranslationEngine selectedEngine = request.PreferredModelAlias switch
        {
            var a when TranslationModelOverrideSettings.IsDeepLModelAlias(a) => deepLCloudEngine,
            var a when TranslationModelOverrideSettings.IsOpenAiGptAlias(a) => openAiCloudEngine,
            var a when TranslationModelOverrideSettings.IsGeminiTranslationAlias(a) => geminiCloudEngine,
            _ => localEngine
        };

        IReadOnlyList<TranslatedTextSegment> translated = await selectedEngine
            .TranslateAsync(request, cancellationToken)
            .ConfigureAwait(false);

        LastExecutionMetadata = selectedEngine is ITranslationExecutionMetadataReporter metadataReporter
            ? metadataReporter.LastExecutionMetadata
            : null;
        LastExecutionSummary = selectedEngine is IStageRuntimeExecutionReporter runtimeReporter
            ? runtimeReporter.LastExecutionSummary
            : null;
        return translated;
    }
}
