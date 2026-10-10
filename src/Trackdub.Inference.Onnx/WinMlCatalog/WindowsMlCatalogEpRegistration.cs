#if WINDOWS
using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.WindowsMl;

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

        CatalogPreparationPhase phase = CatalogPreparationPhase.Register;
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

            CatalogPreparationPlan plan = WindowsMlCatalogPreparationPolicy.Plan(
                WindowsMlEnsureReady.MapReadyState(provider.ReadyState),
                allowProviderDownloads,
                ortExecutionProviderName);
            phase = plan.Phase;
            if (plan.StopWithoutAcquisition)
            {
                return new WinMlCatalogBootstrapResult(
                    false,
                    providerId,
                    WinMlCatalogReadinessBlocker.EpNotPresent,
                    plan.StopDetail ?? $"{ortExecutionProviderName} is not installed.");
            }

            if (plan.CallEnsureReady)
            {
                ExecutionProviderReadyResult readyResult = await WindowsMlEnsureReady
                    .EnsureReadyBoundedAsync(provider, timeoutCts.Token)
                    .ConfigureAwait(false);
                CatalogPreparationOutcome outcome = WindowsMlCatalogPreparationPolicy.Classify(
                    WindowsMlEnsureReady.MapEnsureStatus(readyResult.Status),
                    plan.Phase,
                    ortExecutionProviderName,
                    WindowsMlEnsureReady.FormatHResult(readyResult),
                    readyResult.DiagnosticText);
                if (outcome.Disposition is not CatalogPreparationDisposition.Register)
                {
                    return new WinMlCatalogBootstrapResult(
                        false,
                        providerId,
                        outcome.Disposition is CatalogPreparationDisposition.Pending
                            ? WinMlCatalogReadinessBlocker.EpPreparationPending
                            : plan.Phase is CatalogPreparationPhase.Acquire
                                ? WinMlCatalogReadinessBlocker.EpDownloadFailed
                                : WinMlCatalogReadinessBlocker.EpRegisterFailed,
                        outcome.Detail);
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
            // Only the acquire phase downloads. A timeout while preparing an installed
            // provider, or while registering, is a preparation/registration-check failure.
            WinMlCatalogReadinessBlocker blocker = phase == CatalogPreparationPhase.Acquire
                ? WinMlCatalogReadinessBlocker.EpDownloadFailed
                : WinMlCatalogReadinessBlocker.EpRegisterFailed;
            return new WinMlCatalogBootstrapResult(
                false,
                providerId,
                blocker,
                WindowsMlCatalogPreparationPolicy.TimeoutDetail(ortExecutionProviderName, phase));
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
