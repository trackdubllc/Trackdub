using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Onnx.WindowsMl;

[SupportedOSPlatform("windows10.0.19041.0")]
public sealed class WindowsMlExecutionProviderBootstrapper
{
    public async Task<WindowsMlBootstrapResult> RegisterInstalledCertifiedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryEnsureWinMlProjectionDeployed(out string? deploymentFailure))
        {
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.RegisterInstalledCertified, false, deploymentFailure);
        }

        try
        {
            // Apply a strict deadline to avoid blocking startup indefinitely
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(15));

            var catalog = ExecutionProviderCatalog.GetDefault();
            (bool succeeded, string? detail) = await RegisterCatalogProvidersAsync(catalog, allowDownloads: false, timeoutCts.Token);
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.RegisterInstalledCertified, succeeded, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.RegisterInstalledCertified, false, "Windows ML registration timed out after 15 seconds.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.RegisterInstalledCertified, false, ex.Message);
        }
    }

    public async Task<WindowsMlBootstrapResult> EnsureAndRegisterCertifiedAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!TryEnsureWinMlProjectionDeployed(out string? deploymentFailure))
        {
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.EnsureAndRegisterCertified, false, deploymentFailure);
        }

        try
        {
            // Apply a longer deadline for potential downloads
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromMinutes(5));

            var catalog = ExecutionProviderCatalog.GetDefault();
            (bool succeeded, string? detail) = await RegisterCatalogProvidersAsync(catalog, allowDownloads: true, timeoutCts.Token);
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.EnsureAndRegisterCertified, succeeded, detail);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.EnsureAndRegisterCertified, false, "Windows ML ensure-and-register timed out after 5 minutes.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.EnsureAndRegisterCertified, false, ex.Message);
        }
    }

    /// <summary>
    /// Per-provider equivalent of RegisterCertifiedAsync / EnsureAndRegisterCertifiedAsync that skips
    /// providers Trackdub ships itself (see <see cref="WindowsMlCatalogProviderFilter"/>). Without
    /// downloads, only providers already on the machine are readied and registered, matching
    /// RegisterCertifiedAsync. Like the bulk APIs, one provider failing does not fail the others: the
    /// call fails only when providers failed and none registered; partial failures stay in the detail.
    /// </summary>
    private static async Task<(bool Succeeded, string? Detail)> RegisterCatalogProvidersAsync(
        ExecutionProviderCatalog catalog,
        bool allowDownloads,
        CancellationToken cancellationToken)
    {
        var failures = new List<string>();
        int registered = 0;
        foreach (ExecutionProvider provider in catalog.FindAllProviders())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (WindowsMlCatalogProviderFilter.IsExcludedFromBulkRegistration(provider.Name) ||
                (!allowDownloads && provider.ReadyState is ExecutionProviderReadyState.NotPresent))
            {
                continue;
            }

            if (provider.ReadyState is not ExecutionProviderReadyState.Ready)
            {
                ExecutionProviderReadyResult ready = await provider.EnsureReadyAsync().AsTask(cancellationToken);
                if (ready.Status is not ExecutionProviderReadyResultState.Success)
                {
                    failures.Add($"EnsureReadyAsync failed for {provider.Name}: {ready.Status}.");
                    continue;
                }
            }

            if (provider.TryRegister())
            {
                registered++;
            }
            else
            {
                failures.Add($"TryRegister failed for {provider.Name}.");
            }
        }

        return failures.Count == 0
            ? (true, null)
            : (registered > 0, string.Join(" ", failures));
    }

    private static bool TryEnsureWinMlProjectionDeployed(out string? failureReason)
    {
        string projectionPath = Path.Combine(
            AppContext.BaseDirectory,
            "Microsoft.Windows.AI.MachineLearning.Projection.dll");
        if (File.Exists(projectionPath))
        {
            failureReason = null;
            return true;
        }

        failureReason =
            "Microsoft.Windows.AI.MachineLearning.Projection.dll was not deployed next to the application. Rebuild or reinstall Trackdub so WinML managed assets are copied to the output directory.";
        return false;
    }
}
