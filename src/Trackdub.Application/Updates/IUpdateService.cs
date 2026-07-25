using Trackdub.Contracts.Licensing;

namespace Trackdub.Application.Updates;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);

    Task<UpdateDownloadResult> DownloadUpdateAsync(
        ReleaseEntry release,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> LaunchInstallerAsync(
        string installerPath,
        CancellationToken cancellationToken = default);
}
