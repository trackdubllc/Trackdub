using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

public sealed class TensorRtRtxReadinessProbe : ITensorRtRtxReadinessProbe
{
    private readonly ITensorRtRtxProviderBootstrap _pluginBootstrap;

    public TensorRtRtxReadinessProbe()
        : this(TensorRtRtxPluginService.Shared)
    {
    }

    internal TensorRtRtxReadinessProbe(ITensorRtRtxProviderBootstrap pluginBootstrap)
    {
        _pluginBootstrap = pluginBootstrap ?? throw new ArgumentNullException(nameof(pluginBootstrap));
    }

    public async Task<TensorRtRtxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return await ProbeWindowsAsync(allowProviderDownloads, cancellationToken).ConfigureAwait(false);
        }
#endif

#if LINUX
        if (OperatingSystem.IsLinux())
        {
            return await ProbeLinuxAsync(allowProviderDownloads, cancellationToken).ConfigureAwait(false);
        }
#endif

        return new TensorRtRtxReadinessReport(
            ProviderId: string.Empty,
            TensorRtRtxPlatformRoute.None,
            TensorRtRtxReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "TensorRT RTX EP ABI plugin is supported on Windows and Linux with an NVIDIA GPU.");
    }

#if WINDOWS
    private async Task<TensorRtRtxReadinessReport> ProbeWindowsAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        const string providerId = TensorRtRtxProviderIds.PluginEpAbi;
        (bool eligible, TensorRtRtxReadinessBlocker hardwareBlocker, string hardwareDetail) =
            WindowsNvidiaHardwareGate.Evaluate();

        bool ortListed = TensorRtRtxOrtProbe.IsPluginProviderListed();
        if (!eligible)
        {
            return new TensorRtRtxReadinessReport(
                providerId,
                TensorRtRtxPlatformRoute.PluginEpAbi,
                hardwareBlocker,
                IsHardwareEligible: false,
                IsOrtProviderListed: ortListed,
                IsRegisteredWithOrt: ortListed,
                Detail: hardwareDetail);
        }

        TensorRtRtxBootstrapResult bootstrap = await _pluginBootstrap
            .EnsureRegisteredAsync(allowProviderDownloads, cancellationToken)
            .ConfigureAwait(false);

        ortListed = TensorRtRtxOrtProbe.IsPluginProviderListed();
        TensorRtRtxReadinessBlocker blocker = bootstrap.Succeeded
            ? TensorRtRtxReadinessBlocker.None
            : bootstrap.Blocker ?? TensorRtRtxReadinessBlocker.EpRegisterFailed;

        return new TensorRtRtxReadinessReport(
            providerId,
            TensorRtRtxPlatformRoute.PluginEpAbi,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ortListed,
            IsRegisteredWithOrt: ortListed && bootstrap.Succeeded,
            Detail: bootstrap.Detail);
    }
#endif

#if LINUX
    private async Task<TensorRtRtxReadinessReport> ProbeLinuxAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        const string providerId = TensorRtRtxProviderIds.PluginEpAbi;
        (bool eligible, TensorRtRtxReadinessBlocker hardwareBlocker, string hardwareDetail) =
            LinuxNvidiaHardwareGate.Evaluate();

        bool ortListed = TensorRtRtxOrtProbe.IsPluginProviderListed();
        if (!eligible)
        {
            return new TensorRtRtxReadinessReport(
                providerId,
                TensorRtRtxPlatformRoute.PluginEpAbi,
                hardwareBlocker,
                IsHardwareEligible: false,
                IsOrtProviderListed: ortListed,
                IsRegisteredWithOrt: ortListed,
                Detail: hardwareDetail);
        }

        TensorRtRtxBootstrapResult bootstrap = await _pluginBootstrap
            .EnsureRegisteredAsync(allowProviderDownloads, cancellationToken)
            .ConfigureAwait(false);

        ortListed = TensorRtRtxOrtProbe.IsPluginProviderListed();
        TensorRtRtxReadinessBlocker blocker = bootstrap.Succeeded
            ? TensorRtRtxReadinessBlocker.None
            : bootstrap.Blocker ?? TensorRtRtxReadinessBlocker.EpRegisterFailed;

        return new TensorRtRtxReadinessReport(
            providerId,
            TensorRtRtxPlatformRoute.PluginEpAbi,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ortListed,
            IsRegisteredWithOrt: ortListed && bootstrap.Succeeded,
            Detail: bootstrap.Detail);
    }
#endif
}
