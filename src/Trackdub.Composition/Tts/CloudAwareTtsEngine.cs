using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Tts;

public sealed class CloudAwareTtsEngine(
    ITtsEngine localEngine,
    ITtsEngine elevenLabsCloudEngine,
    ITtsEngine openAiCloudEngine,
    ITtsEngine googleCloudEngine)
    : ITtsEngine, IStageRuntimeExecutionReporter
{
    private readonly ITtsEngine localEngine = localEngine ?? throw new ArgumentNullException(nameof(localEngine));
    private readonly ITtsEngine elevenLabsCloudEngine = elevenLabsCloudEngine ?? throw new ArgumentNullException(nameof(elevenLabsCloudEngine));
    private readonly ITtsEngine openAiCloudEngine = openAiCloudEngine ?? throw new ArgumentNullException(nameof(openAiCloudEngine));
    private readonly ITtsEngine googleCloudEngine = googleCloudEngine ?? throw new ArgumentNullException(nameof(googleCloudEngine));

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? alias = request.Options?.NormalizedPreferredModelAlias;

        ITtsEngine selectedEngine = alias switch
        {
            var a when TtsModelOverrideSettings.IsElevenLabsAlias(a) => elevenLabsCloudEngine,
            var a when TtsModelOverrideSettings.IsOpenAiTtsAlias(a) => openAiCloudEngine,
            var a when TtsModelOverrideSettings.IsGoogleTtsAlias(a) => googleCloudEngine,
            _ => localEngine
        };

        TtsSynthesisResult result = await selectedEngine
            .SynthesizeAsync(request, cancellationToken)
            .ConfigureAwait(false);

        LastExecutionSummary = selectedEngine is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;

        return result;
    }
}
