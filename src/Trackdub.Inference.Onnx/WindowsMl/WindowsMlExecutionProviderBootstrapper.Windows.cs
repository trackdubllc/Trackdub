using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;

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
            await catalog.RegisterCertifiedAsync().AsTask(timeoutCts.Token);
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.RegisterInstalledCertified, true, null);
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
            await catalog.EnsureAndRegisterCertifiedAsync().AsTask(timeoutCts.Token);
            return new WindowsMlBootstrapResult(WindowsMlBootstrapMode.EnsureAndRegisterCertified, true, null);
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
