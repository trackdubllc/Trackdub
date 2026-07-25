using Trackdub.Contracts;

namespace Trackdub.Media.Process;

public sealed class FfmpegExplicitInstaller : IExplicitFfmpegInstaller
{
    private readonly FfmpegAutoDownloader downloader;

    public FfmpegExplicitInstaller()
    {
        downloader = FfmpegAutoDownloader.Shared;
    }

    internal FfmpegExplicitInstaller(FfmpegAutoDownloader downloader)
    {
        this.downloader = downloader ?? throw new ArgumentNullException(nameof(downloader));
    }

    public async Task<bool> InstallFfmpegAsync(CancellationToken ct = default)
    {
        return await downloader.InstallExplicitAsync(ct).ConfigureAwait(false);
    }
}
