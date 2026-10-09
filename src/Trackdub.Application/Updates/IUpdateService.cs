using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;

namespace Trackdub.Application.Updates;

public interface IUpdateService
{
    Task<UpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Channel-aware update check. The default implementation forwards to the
    /// Stable feed so existing callers keep working; the infrastructure
    /// <c>UpdateService</c> resolves a per-channel manifest URL instead.
    /// </summary>
    Task<UpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        UpdateChannel channel,
        CancellationToken cancellationToken = default)
        => CheckForUpdateAsync(currentVersion, cancellationToken);

    Task<UpdateDownloadResult> DownloadUpdateAsync(
        ReleaseEntry release,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<bool> LaunchInstallerAsync(
        string installerPath,
        CancellationToken cancellationToken = default);
}
