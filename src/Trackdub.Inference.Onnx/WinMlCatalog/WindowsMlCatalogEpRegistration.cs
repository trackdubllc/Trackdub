#if WINDOWS
using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsMlCatalogEpRegistration
{
    public static async Task<WinMlCatalogBootstrapResult> EnsureRegisteredAsync(
        string ortExecutionProviderName,
        string providerId,
        Func<(bool Eligible, WinMlCatalogReadinessBlocker Blocker, string Detail)> hardwareGate,
        Func<bool> isOrtProviderListed,
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (bool eligible, WinMlCatalogReadinessBlocker hardwareBlocker, string hardwareDetail) = hardwareGate();
        if (!eligible)
        {
            return new WinMlCatalogBootstrapResult(false, providerId, hardwareBlocker, hardwareDetail);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(allowProviderDownloads ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));

            ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
            ExecutionProvider? provider = catalog.FindAllProviders()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.Name, ortExecutionProviderName, StringComparison.Ordinal));

            if (provider is null)
            {
                return new WinMlCatalogBootstrapResult(
                    false,
                    providerId,
                    WinMlCatalogReadinessBlocker.EpNotPresent,
                    $"{ortExecutionProviderName} is not offered by the Windows ML catalog on this machine.");
            }

            if (provider.ReadyState is ExecutionProviderReadyState.NotPresent or ExecutionProviderReadyState.NotReady)
            {
                if (!allowProviderDownloads)
                {
                    WinMlCatalogReadinessBlocker blocker = provider.ReadyState is ExecutionProviderReadyState.NotPresent
                        ? WinMlCatalogReadinessBlocker.EpNotPresent
                        : WinMlCatalogReadinessBlocker.EpNotReady;
                    return new WinMlCatalogBootstrapResult(
                        false,
                        providerId,
                        blocker,
                        $"{ortExecutionProviderName} is not ready. Use Install provider in Model Manager.");
                }

                ExecutionProviderReadyResult readyResult = await provider.EnsureReadyAsync()
                    .AsTask(timeoutCts.Token)
                    .ConfigureAwait(false);
                if (readyResult.Status is not ExecutionProviderReadyResultState.Success)
                {
                    return new WinMlCatalogBootstrapResult(
                        false,
                        providerId,
                        WinMlCatalogReadinessBlocker.EpDownloadFailed,
                        $"EnsureReadyAsync failed for {ortExecutionProviderName}: {readyResult.Status}.");
                }
            }

            if (!provider.TryRegister())
            {
                return new WinMlCatalogBootstrapResult(
                    false,
                    providerId,
                    WinMlCatalogReadinessBlocker.EpRegisterFailed,
                    $"TryRegister failed for {ortExecutionProviderName}.");
            }

            if (!isOrtProviderListed())
            {
                return new WinMlCatalogBootstrapResult(
                    false,
                    providerId,
                    WinMlCatalogReadinessBlocker.OrtProviderUnavailable,
                    $"{ortExecutionProviderName} registered with catalog but ONNX Runtime does not list the provider.");
            }

            return new WinMlCatalogBootstrapResult(
                true,
                providerId,
                null,
                $"{ortExecutionProviderName} registered with ONNX Runtime.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            // When downloads are allowed the timeout means the download/install stalled → EpDownloadFailed.
            // When downloads are disabled we only checked readiness and it timed out → EpRegisterFailed
            // (not EpDownloadFailed, which would be misleading since no download was attempted).
            WinMlCatalogReadinessBlocker blocker = allowProviderDownloads
                ? WinMlCatalogReadinessBlocker.EpDownloadFailed
                : WinMlCatalogReadinessBlocker.EpRegisterFailed;
            string detail = allowProviderDownloads
                ? $"{ortExecutionProviderName} catalog registration timed out."
                : $"{ortExecutionProviderName} catalog readiness check timed out. Use Install provider in Model Manager.";
            return new WinMlCatalogBootstrapResult(false, providerId, blocker, detail);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WinMlCatalogBootstrapResult(
                false,
                providerId,
                WinMlCatalogReadinessBlocker.EpRegisterFailed,
                ex.Message);
        }
    }
}
#endif
