namespace Trackdub.Contracts;

public sealed record FfmpegHealthStatus(
    bool FfmpegAvailable,
    string? FfmpegPath,
    bool FfprobeAvailable,
    string? FfprobePath,
    string? ErrorMessage);

public interface IFfmpegHealthCheck
{
    FfmpegHealthStatus CheckAvailability();
}
