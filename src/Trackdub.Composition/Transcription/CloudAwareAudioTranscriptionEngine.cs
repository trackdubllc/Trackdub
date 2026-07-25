using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Composition.Transcription;

public sealed class CloudAwareAudioTranscriptionEngine(
    IAudioTranscriptionEngine localEngine,
    IAudioTranscriptionEngine openAiCloudEngine,
    IAudioTranscriptionEngine geminiCloudEngine)
    : IAudioTranscriptionEngine, IStageRuntimeExecutionReporter
{
    private readonly IAudioTranscriptionEngine localEngine = localEngine ?? throw new ArgumentNullException(nameof(localEngine));
    private readonly IAudioTranscriptionEngine openAiCloudEngine = openAiCloudEngine ?? throw new ArgumentNullException(nameof(openAiCloudEngine));
    private readonly IAudioTranscriptionEngine geminiCloudEngine = geminiCloudEngine ?? throw new ArgumentNullException(nameof(geminiCloudEngine));

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken) =>
        TranscribeAsync(new AudioTranscriptionRequest(normalizedAudioPath, regions), cancellationToken);

    public async Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string? alias = request.Options?.NormalizedPreferredModelAlias;

        IAudioTranscriptionEngine selectedEngine = alias switch
        {
            var a when AsrModelOverrideSettings.IsOpenAiWhisperAlias(a) => openAiCloudEngine,
            var a when AsrModelOverrideSettings.IsGeminiAsrAlias(a) => geminiCloudEngine,
            _ => localEngine
        };

        IReadOnlyList<RecognizedTranscriptSegment> result = await selectedEngine
            .TranscribeAsync(request, cancellationToken)
            .ConfigureAwait(false);

        LastExecutionSummary = selectedEngine is IStageRuntimeExecutionReporter reporter
            ? reporter.LastExecutionSummary
            : null;

        return result;
    }
}
