using Trackdub.Application.Runtime;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

/// <summary>
/// Downloads and extracts the pinned TensorRT RTX EP ABI provider bundle into the standard
/// Trackdub provider directory. Does not register the plugin with ONNX Runtime.
/// </summary>
public sealed class TrtRtxEpBundleInstaller(
    TrackdubStoragePaths storagePaths,
    TrtRtxEpBundleDownloader bundleDownloader,
    IStudioSettingsService settingsService,
    IApplicationLogger logger) : ITrtRtxEpBundleInstaller
{
    private readonly TrtRtxEpBundleManifest _manifest = LoadManifest();

    public async Task<TrtRtxEpBundleInstallResult> EnsureBundleAsync(
        IProgress<string> progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(progress);

        if (!OperatingSystem.IsWindows() && !OperatingSystem.IsLinux())
        {
            return new TrtRtxEpBundleInstallResult(
                Succeeded: false,
                InstallDirectory: null,
                FailureDetail: "TensorRT RTX EP ABI bundle install is supported on Windows and Linux only.");
        }

        StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.NvidiaTensorRtRtxLicenseAccepted)
        {
            return new TrtRtxEpBundleInstallResult(
                Succeeded: false,
                InstallDirectory: null,
                FailureDetail:
                    "NVIDIA TensorRT RTX license has not been accepted. Review the license in Model Manager and install the plugin explicitly.");
        }

        try
        {
            string installDirectory = await bundleDownloader
                .DownloadAndInstallAsync(
                    storagePaths.UserDataRoot,
                    _manifest,
                    TrtRtxEpRequiredFiles.RequiredFileNames,
                    progress,
                    cancellationToken)
                .ConfigureAwait(false);

            settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!string.Equals(settings.TensorRtRtxPluginDirectory, installDirectory, StringComparison.OrdinalIgnoreCase))
            {
                StudioSettings updated = settings with { TensorRtRtxPluginDirectory = installDirectory };
                await settingsService.SaveAsync(updated, cancellationToken).ConfigureAwait(false);
                logger.LogInformation($"Persisted TensorRtRtxPluginDirectory='{installDirectory}'.");
            }

            return new TrtRtxEpBundleInstallResult(Succeeded: true, InstallDirectory: installDirectory);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            string detail = $"TensorRT RTX EP bundle download failed: {ex.Message}";
            progress.Report(detail);
            return new TrtRtxEpBundleInstallResult(Succeeded: false, InstallDirectory: null, FailureDetail: detail);
        }
    }

    private static TrtRtxEpBundleManifest LoadManifest()
    {
        string manifestPath = Path.Combine(AppContext.BaseDirectory, "trt-rtx-ep.manifest.json");
        return TrtRtxEpBundleManifestLoader.Load(manifestPath);
    }
}
