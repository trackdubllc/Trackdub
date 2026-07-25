using Trackdub.Contracts;

namespace Trackdub.Media.Process;

public sealed class FfmpegExportToolAvailabilityService : IExportToolAvailabilityService
{
    private readonly IFfmpegToolResolver toolResolver;

    public FfmpegExportToolAvailabilityService(string? ffmpegPath = null, string? ffprobePath = null)
    {
        toolResolver = new FfmpegToolResolver(ffmpegPath, ffprobePath);
    }

    internal FfmpegExportToolAvailabilityService(IFfmpegToolResolver toolResolver)
    {
        this.toolResolver = toolResolver ?? throw new ArgumentNullException(nameof(toolResolver));
    }

    public ExportToolAvailability CheckAvailability()
    {
        try
        {
            string ffmpegPath = toolResolver.ResolveFfmpegPath(allowAutoDownload: false);
            string ffprobePath = toolResolver.ResolveFfprobePath(allowAutoDownload: false);
            return ExportToolAvailability.Available(ffmpegPath, ffprobePath);
        }
        catch (Exception ex) when (ex is InvalidOperationException or ArgumentException or IOException or UnauthorizedAccessException)
        {
            return ExportToolAvailability.Unavailable(ex.Message);
        }
    }
}
