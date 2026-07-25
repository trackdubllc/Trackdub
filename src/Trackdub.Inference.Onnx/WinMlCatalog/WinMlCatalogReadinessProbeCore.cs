using Trackdub.Contracts.ApplicationContracts;
#if WINDOWS
using Trackdub.Inference.Onnx.OpenVino;
using Trackdub.Inference.Onnx.Qnn;
using Trackdub.Inference.Onnx.VitisAi;
#endif

namespace Trackdub.Inference.Onnx.WinMlCatalog;

internal static class WinMlCatalogReadinessProbeCore
{
#if WINDOWS
    public static async Task<WinMlCatalogReadinessReport> ProbeWindowsAsync(
        string providerId,
        Func<(bool Eligible, WinMlCatalogReadinessBlocker Blocker, string Detail)> hardwareGate,
        Func<bool> isOrtProviderListed,
        Func<bool, CancellationToken, Task<WinMlCatalogBootstrapResult>> ensureRegisteredAsync,
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        (bool eligible, WinMlCatalogReadinessBlocker hardwareBlocker, string hardwareDetail) = hardwareGate();
        bool ortListed = isOrtProviderListed();
        if (!eligible)
        {
            return new WinMlCatalogReadinessReport(
                providerId,
                WinMlCatalogPlatformRoute.WinMlCatalog,
                hardwareBlocker,
                IsHardwareEligible: false,
                IsOrtProviderListed: ortListed,
                IsRegisteredWithOrt: ortListed,
                Detail: hardwareDetail);
        }

        WinMlCatalogBootstrapResult bootstrap = await ensureRegisteredAsync(false, cancellationToken)
            .ConfigureAwait(false);
        if (!bootstrap.Succeeded &&
            bootstrap.Blocker is WinMlCatalogReadinessBlocker.EpNotPresent &&
            allowProviderDownloads)
        {
            bootstrap = await ensureRegisteredAsync(true, cancellationToken).ConfigureAwait(false);
        }

        ortListed = isOrtProviderListed();
        WinMlCatalogReadinessBlocker blocker = bootstrap.Succeeded
            ? WinMlCatalogReadinessBlocker.None
            : bootstrap.Blocker ?? WinMlCatalogReadinessBlocker.EpRegisterFailed;

        return new WinMlCatalogReadinessReport(
            providerId,
            WinMlCatalogPlatformRoute.WinMlCatalog,
            blocker,
            IsHardwareEligible: true,
            IsOrtProviderListed: ortListed,
            IsRegisteredWithOrt: ortListed && bootstrap.Succeeded,
            Detail: bootstrap.Detail);
    }
#endif
}

public sealed class OpenVinoCatalogReadinessProbe : IOpenVinoCatalogReadinessProbe
{
#if WINDOWS
    private readonly WindowsMlOpenVinoCatalogService _catalog = new();
#endif

    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return WinMlCatalogReadinessProbeCore.ProbeWindowsAsync(
                OpenVinoCatalogProviderIds.WinMl,
                WindowsIntelCatalogHardwareGate.Evaluate,
                OpenVinoCatalogOrtProbe.IsProviderListed,
                _catalog.EnsureRegisteredAsync,
                allowProviderDownloads,
                cancellationToken);
        }
#endif

        return Task.FromResult(Unsupported());
    }

    private static WinMlCatalogReadinessReport Unsupported() =>
        new(
            OpenVinoCatalogProviderIds.WinMl,
            WinMlCatalogPlatformRoute.None,
            WinMlCatalogReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "OpenVINO catalog EP is supported on Windows only.");
}

public sealed class QnnCatalogReadinessProbe : IQnnCatalogReadinessProbe
{
#if WINDOWS
    private readonly WindowsMlQnnCatalogService _catalog = new();
#endif

    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return WinMlCatalogReadinessProbeCore.ProbeWindowsAsync(
                QnnProviderIds.WinMl,
                WindowsQualcommCatalogHardwareGate.Evaluate,
                QnnOrtProbe.IsProviderListed,
                _catalog.EnsureRegisteredAsync,
                allowProviderDownloads,
                cancellationToken);
        }
#endif

        return Task.FromResult(Unsupported());
    }

    private static WinMlCatalogReadinessReport Unsupported() =>
        new(
            QnnProviderIds.WinMl,
            WinMlCatalogPlatformRoute.None,
            WinMlCatalogReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "QNN catalog EP is supported on Windows only.");
}

public sealed class VitisAiCatalogReadinessProbe : IVitisAiCatalogReadinessProbe
{
#if WINDOWS
    private readonly WindowsMlVitisAiCatalogService _catalog = new();
#endif

    public Task<WinMlCatalogReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

#if WINDOWS
        if (OperatingSystem.IsWindows())
        {
            return WinMlCatalogReadinessProbeCore.ProbeWindowsAsync(
                VitisAiProviderIds.WinMl,
                WindowsAmdNpuCatalogHardwareGate.Evaluate,
                VitisAiOrtProbe.IsProviderListed,
                _catalog.EnsureRegisteredAsync,
                allowProviderDownloads,
                cancellationToken);
        }
#endif

        return Task.FromResult(Unsupported());
    }

    private static WinMlCatalogReadinessReport Unsupported() =>
        new(
            VitisAiProviderIds.WinMl,
            WinMlCatalogPlatformRoute.None,
            WinMlCatalogReadinessBlocker.PlatformUnsupported,
            IsHardwareEligible: false,
            IsOrtProviderListed: false,
            IsRegisteredWithOrt: false,
            Detail: "Vitis AI catalog EP is supported on Windows only.");
}
