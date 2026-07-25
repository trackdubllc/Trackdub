namespace Trackdub.Contracts;

public interface IAudioTimeStretchService
{
    Task<AudioTimeStretchResult> StretchAsync(
        AudioTimeStretchRequest request,
        CancellationToken cancellationToken);
}

public sealed record AudioTimeStretchRequest(
    string InputPath,
    string OutputPath,
    double TempoRatio,
    bool EnableRubberband,
    double RubberbandThreshold);

public sealed record AudioTimeStretchResult(
    Trackdub.Domain.Tts.TtsStretchEngine Engine,
    bool UsedFallback,
    string? Message = null);
