namespace Trackdub.Contracts.Pipeline;

public interface ISpeakerDiarizationEngine
{
    Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        string normalizedAudioPath,
        double durationSeconds,
        IReadOnlyList<SpeechRegion> speechRegions,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<DiarizedSpeakerTurn>> DiarizeAsync(
        SpeakerDiarizationRequest request,
        CancellationToken cancellationToken) =>
        DiarizeAsync(
            request.NormalizedAudioPath,
            request.DurationSeconds,
            request.SpeechRegions,
            cancellationToken);
}

public sealed record SpeakerDiarizationRequest(
    string NormalizedAudioPath,
    double DurationSeconds,
    IReadOnlyList<SpeechRegion> SpeechRegions,
    InferenceRequestOptions? Options = null);

public sealed record DiarizedSpeakerTurn(
    string? SpeakerKey,
    double StartSeconds,
    double EndSeconds,
    double? Confidence = null,
    bool HasOverlap = false)
{
    public string NormalizedSpeakerKey =>
        string.IsNullOrWhiteSpace(SpeakerKey)
            ? "speaker-unknown"
            : SpeakerKey.Trim();
}
