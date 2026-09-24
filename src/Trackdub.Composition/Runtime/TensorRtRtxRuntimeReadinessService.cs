using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Composition.Runtime;

public sealed class TensorRtRtxRuntimeReadinessService(ITensorRtRtxReadinessProbe readinessProbe)
    : ITensorRtRtxRuntimeReadinessService
{
    private readonly ITensorRtRtxReadinessProbe readinessProbe =
        readinessProbe ?? throw new ArgumentNullException(nameof(readinessProbe));

    public async Task<TensorRtRtxRuntimeReadinessSnapshot> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        TensorRtRtxReadinessReport report = await readinessProbe
            .ProbeAsync(allowProviderDownloads, cancellationToken)
            .ConfigureAwait(false);

        if (report.Route is TensorRtRtxPlatformRoute.None)
        {
            return new TensorRtRtxRuntimeReadinessSnapshot(
                IsSupportedPlatform: false,
                IsReady: false,
                ProviderId: null,
                RouteDisplay: "Not supported",
                StatusLabel: "Unavailable",
                Blocker: report.Blocker,
                Detail: report.Detail,
                IsHardwareEligible: report.IsHardwareEligible,
                IsOrtProviderListed: report.IsOrtProviderListed,
                IsRegisteredWithOrt: report.IsRegisteredWithOrt,
                CanInstallWinMlProvider: false,
                InstallHint: null);
        }

        if (report.IsReady)
        {
            return new TensorRtRtxRuntimeReadinessSnapshot(
                IsSupportedPlatform: true,
                IsReady: true,
                ProviderId: report.ProviderId,
                RouteDisplay: FormatRoute(report.Route),
                StatusLabel: "Ready",
                Blocker: TensorRtRtxReadinessBlocker.None,
                Detail: report.Detail,
                IsHardwareEligible: report.IsHardwareEligible,
                IsOrtProviderListed: report.IsOrtProviderListed,
                IsRegisteredWithOrt: report.IsRegisteredWithOrt,
                CanInstallWinMlProvider: false,
                InstallHint: null);
        }

        bool canInstall = report.Route is TensorRtRtxPlatformRoute.PluginEpAbi &&
                          report.Blocker is TensorRtRtxReadinessBlocker.EpNotPresent
                              or TensorRtRtxReadinessBlocker.EpNotReady
                              or TensorRtRtxReadinessBlocker.EpDownloadFailed
                              or TensorRtRtxReadinessBlocker.EpRegisterFailed
                              or TensorRtRtxReadinessBlocker.OrtProviderUnavailable;

        string? installHint = report.Route switch
        {
            // The cu13 bundle ships its CUDA runtime (static on Windows, libcudart.so.13 on Linux),
            // so a missing runtime means a damaged bundle rather than a missing system toolkit.
            TensorRtRtxPlatformRoute.PluginEpAbi when report.Blocker is TensorRtRtxReadinessBlocker.CudaRuntimeMissing =>
                "Reinstall the TensorRT RTX EP bundle from Model Manager (it ships the CUDA 13 runtime), "
                + $"or set {TensorRtRtxProviderConstants.CudaRuntimeBinDirectoryEnvironmentVariable} to a directory containing a CUDA 13 runtime.",
            TensorRtRtxPlatformRoute.PluginEpAbi when canInstall =>
                OperatingSystem.IsLinux()
                    ? TensorRtRtxProviderConstants.LinuxInstallHint
                    : TensorRtRtxProviderConstants.WindowsInstallHint,
            TensorRtRtxPlatformRoute.NativeTensorRt =>
                TensorRtRtxProviderConstants.LinuxInstallHint,
            _ => null
        };

        return new TensorRtRtxRuntimeReadinessSnapshot(
            IsSupportedPlatform: true,
            IsReady: false,
            ProviderId: string.IsNullOrWhiteSpace(report.ProviderId) ? null : report.ProviderId,
            RouteDisplay: FormatRoute(report.Route),
            StatusLabel: FormatBlockedStatus(report.Blocker),
            Blocker: report.Blocker,
            Detail: report.Detail,
            IsHardwareEligible: report.IsHardwareEligible,
            IsOrtProviderListed: report.IsOrtProviderListed,
            IsRegisteredWithOrt: report.IsRegisteredWithOrt,
            CanInstallWinMlProvider: canInstall,
            InstallHint: installHint);
    }

    private static string FormatRoute(TensorRtRtxPlatformRoute route) =>
        route switch
        {
            TensorRtRtxPlatformRoute.PluginEpAbi when OperatingSystem.IsLinux() => "Linux (EP ABI plugin)",
            TensorRtRtxPlatformRoute.PluginEpAbi => "Windows (EP ABI plugin)",
            TensorRtRtxPlatformRoute.NativeTensorRt => "Linux (native TensorRT)",
            _ => "Not supported"
        };

    private static string FormatBlockedStatus(TensorRtRtxReadinessBlocker blocker) =>
        blocker switch
        {
            TensorRtRtxReadinessBlocker.None => "Not ready",
            TensorRtRtxReadinessBlocker.EpNotPresent => "Plugin missing",
            TensorRtRtxReadinessBlocker.EpDownloadFailed => "Download failed",
            TensorRtRtxReadinessBlocker.EpNotReady => "Setup required",
            TensorRtRtxReadinessBlocker.GpuVendorMismatch => "No NVIDIA GPU",
            TensorRtRtxReadinessBlocker.OrtProviderUnavailable => "ORT EP missing",
            TensorRtRtxReadinessBlocker.EpRegisterFailed => "Registration failed",
            TensorRtRtxReadinessBlocker.CudaRuntimeMissing => "CUDA runtime missing",
            _ => "Blocked"
        };
}
