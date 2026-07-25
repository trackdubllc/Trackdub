using Trackdub.Contracts;

namespace Trackdub.Media.Process;

public sealed class FfmpegHealthCheck : IFfmpegHealthCheck
{
    private readonly IFfmpegToolResolver toolResolver;

    public FfmpegHealthCheck(string? ffmpegPath = null, string? ffprobePath = null)
    {
        toolResolver = new FfmpegToolResolver(ffmpegPath, ffprobePath);
    }

    internal FfmpegHealthCheck(IFfmpegToolResolver toolResolver)
    {
        this.toolResolver = toolResolver ?? throw new ArgumentNullException(nameof(toolResolver));
    }

    public FfmpegHealthStatus CheckAvailability()
    {
        string? ffmpegPath = null;
        string? ffprobePath = null;
        string? errorMessage = null;

        try
        {
            ffmpegPath = toolResolver.ResolveFfmpegPath(allowAutoDownload: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            errorMessage = ex.Message;
        }

        try
        {
            ffprobePath = toolResolver.ResolveFfprobePath(allowAutoDownload: false);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            errorMessage = errorMessage is null ? ex.Message : $"{errorMessage}; {ex.Message}";
        }

        return new FfmpegHealthStatus(
            FfmpegAvailable: ffmpegPath is not null,
            FfmpegPath: ffmpegPath,
            FfprobeAvailable: ffprobePath is not null,
            FfprobePath: ffprobePath,
            ErrorMessage: errorMessage);
    }
}
