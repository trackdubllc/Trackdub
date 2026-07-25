using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsMlMigraphxCatalogService
{
    public async Task<MigraphxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        (bool eligible, MigraphxReadinessBlocker hardwareBlocker, string hardwareDetail) = WindowsMigraphxHardwareGate.Evaluate();
        if (!eligible)
        {
            return new MigraphxBootstrapResult(false, MigraphxProviderIds.WinMl, hardwareBlocker, hardwareDetail);
        }

        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(allowProviderDownloads ? TimeSpan.FromMinutes(5) : TimeSpan.FromSeconds(30));

            ExecutionProviderCatalog catalog = ExecutionProviderCatalog.GetDefault();
            ExecutionProvider? migraphx = catalog.FindAllProviders()
                .FirstOrDefault(provider =>
                    string.Equals(provider.Name, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.Ordinal));

            if (migraphx is null)
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.EpNotPresent,
                    $"{MigraphxProviderConstants.OrtExecutionProviderName} is not offered by the Windows ML catalog on this machine.");
            }

            if (migraphx.ReadyState is ExecutionProviderReadyState.NotPresent)
            {
                if (!allowProviderDownloads)
                {
                    return new MigraphxBootstrapResult(
                        false,
                        MigraphxProviderIds.WinMl,
                        MigraphxReadinessBlocker.EpNotPresent,
                        $"{MigraphxProviderConstants.OrtExecutionProviderName} is not installed. Enable provider downloads in Model Manager.");
                }

                ExecutionProviderReadyResult readyResult = await migraphx.EnsureReadyAsync().AsTask(timeoutCts.Token)
                    .ConfigureAwait(false);
                if (readyResult.Status is not ExecutionProviderReadyResultState.Success)
                {
                    return new MigraphxBootstrapResult(
                        false,
                        MigraphxProviderIds.WinMl,
                        MigraphxReadinessBlocker.EpDownloadFailed,
                        $"EnsureReadyAsync failed for {MigraphxProviderConstants.OrtExecutionProviderName}: {readyResult.Status}.");
                }
            }
            else if (migraphx.ReadyState is ExecutionProviderReadyState.NotReady)
            {
                if (!allowProviderDownloads)
                {
                    return new MigraphxBootstrapResult(
                        false,
                        MigraphxProviderIds.WinMl,
                        MigraphxReadinessBlocker.EpNotPresent,
                        $"{MigraphxProviderConstants.OrtExecutionProviderName} is not ready. Enable provider downloads in Model Manager.");
                }

                ExecutionProviderReadyResult readyResult = await migraphx.EnsureReadyAsync().AsTask(timeoutCts.Token)
                    .ConfigureAwait(false);
                if (readyResult.Status is not ExecutionProviderReadyResultState.Success)
                {
                    return new MigraphxBootstrapResult(
                        false,
                        MigraphxProviderIds.WinMl,
                        MigraphxReadinessBlocker.EpDownloadFailed,
                        $"EnsureReadyAsync failed while preparing {MigraphxProviderConstants.OrtExecutionProviderName}: {readyResult.Status}.");
                }
            }

            if (!migraphx.TryRegister())
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.EpRegisterFailed,
                    $"TryRegister failed for {MigraphxProviderConstants.OrtExecutionProviderName}.");
            }

            if (!MigraphxOrtProbe.IsProviderListed())
            {
                return new MigraphxBootstrapResult(
                    false,
                    MigraphxProviderIds.WinMl,
                    MigraphxReadinessBlocker.OrtProviderUnavailable,
                    $"{MigraphxProviderConstants.OrtExecutionProviderName} registered with catalog but ONNX Runtime does not list the provider.");
            }

            return new MigraphxBootstrapResult(
                true,
                MigraphxProviderIds.WinMl,
                null,
                "MIGraphX execution provider registered with ONNX Runtime.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new MigraphxBootstrapResult(
                false,
                MigraphxProviderIds.WinMl,
                MigraphxReadinessBlocker.EpDownloadFailed,
                "MIGraphX catalog registration timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new MigraphxBootstrapResult(
                false,
                MigraphxProviderIds.WinMl,
                MigraphxReadinessBlocker.EpRegisterFailed,
                ex.Message);
        }
    }
}
