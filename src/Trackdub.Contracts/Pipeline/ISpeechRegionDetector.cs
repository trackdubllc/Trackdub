namespace Trackdub.Contracts.Pipeline;

public interface ISpeechRegionDetector
{
    Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        string normalizedAudioPath,
        double durationSeconds,
        CancellationToken cancellationToken);

    Task<IReadOnlyList<SpeechRegion>> DetectAsync(
        SpeechRegionDetectionRequest request,
        CancellationToken cancellationToken) =>
        DetectAsync(request.NormalizedAudioPath, request.DurationSeconds, cancellationToken);
}

public sealed record SpeechRegionDetectionRequest(
    string NormalizedAudioPath,
    double DurationSeconds,
    InferenceRequestOptions? Options = null);
