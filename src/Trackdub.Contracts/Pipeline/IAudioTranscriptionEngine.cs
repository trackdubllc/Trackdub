namespace Trackdub.Contracts.Pipeline;

public interface IAudioTranscriptionEngine
{
    Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        string normalizedAudioPath,
        IReadOnlyList<SpeechRegion> regions,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<RecognizedTranscriptSegment>> TranscribeAsync(
        AudioTranscriptionRequest request,
        CancellationToken cancellationToken) =>
        TranscribeAsync(request.NormalizedAudioPath, request.Regions, cancellationToken);
}

public sealed record AudioTranscriptionRequest(
    string NormalizedAudioPath,
    IReadOnlyList<SpeechRegion> Regions,
    InferenceRequestOptions? Options = null,
    string? SourceLanguage = null);
