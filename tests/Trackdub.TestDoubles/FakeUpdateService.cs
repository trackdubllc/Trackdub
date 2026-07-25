using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using ContractsUpdateCheckResult = Trackdub.Contracts.UpdateCheckResult;
using UpdatesUpdateCheckResult = Trackdub.Application.Updates.UpdateCheckResult;
using BannerUpdateCheckResult = Trackdub.Contracts.UpdateCheckResult;
using InstallerReleaseEntry = Trackdub.Application.Updates.ReleaseEntry;
using InstallerUpdateCheckResult = Trackdub.Application.Updates.UpdateCheckResult;
using UpdateDownloadResult = Trackdub.Application.Updates.UpdateDownloadResult;

namespace Trackdub.TestDoubles;

public sealed class FakeUpdateService :
    Trackdub.Application.Services.IUpdateService,
    Trackdub.Application.Updates.IUpdateService
{
    public int ManifestCheckForUpdatesCallCount { get; private set; }
    public int CheckForUpdateCallCount { get; private set; }
    public int DownloadUpdateCallCount { get; private set; }
    public int LaunchInstallerCallCount { get; private set; }
    public BannerUpdateCheckResult? NextResult { get; set; }
    public Exception? CheckException { get; set; }

    public string? LastCheckedVersion { get; private set; }
    public string? LastLaunchedInstallerPath { get; private set; }

    public ContractsUpdateCheckResult? ManifestCheckResult { get; set; }
    public InstallerUpdateCheckResult? CheckResult { get; set; }
    public UpdateDownloadResult? DownloadResult { get; set; }
    public bool LaunchResult { get; set; } = true;

    public Task<BannerUpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        _ = currentVersion;

        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<BannerUpdateCheckResult>(cancellationToken);

        ManifestCheckForUpdatesCallCount++;
        if (CheckException is not null)
            return Task.FromException<BannerUpdateCheckResult>(CheckException);
        return Task.FromResult(
            ManifestCheckResult ?? new BannerUpdateCheckResult(
                AvailableVersion: "0.0.0.0",
                ReleaseNotesUrl: null,
                DownloadUrl: null,
                Channel: channel,
                IsUpdateAvailable: false));
    }

    public Task<InstallerUpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<InstallerUpdateCheckResult>(cancellationToken);
        CheckForUpdateCallCount++;
        LastCheckedVersion = currentVersion;
        return Task.FromResult(CheckResult ?? new InstallerUpdateCheckResult(false, null, null));
    }

    public Task<UpdateDownloadResult> DownloadUpdateAsync(
        InstallerReleaseEntry release,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<UpdateDownloadResult>(cancellationToken);
        DownloadUpdateCallCount++;
        return Task.FromResult(DownloadResult ?? new UpdateDownloadResult(true, "/fake/path/setup.exe", null));
    }

    public Task<bool> LaunchInstallerAsync(
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
            return Task.FromCanceled<bool>(cancellationToken);
        LaunchInstallerCallCount++;
        LastLaunchedInstallerPath = installerPath;
        return Task.FromResult(LaunchResult);
    }
}
