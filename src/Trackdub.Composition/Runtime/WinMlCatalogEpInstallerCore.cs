using Trackdub.Application.Runtime;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Domain;
#if WINDOWS
using Trackdub.Inference.Onnx.WinMlCatalog;
using Trackdub.Inference.Runtime.WinMlCatalog;
#endif

namespace Trackdub.Composition.Runtime;

internal static class WinMlCatalogEpInstallerCore
{
#if WINDOWS
    internal static async Task<WinMlCatalogEpInstallResult> EnsureCoreAsync(
        ExecutionProviderKind provider,
        string displayName,
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        progress.Report($"Contacting Windows ML catalog for {displayName} runtime...");

        WinMlCatalogBootstrapResult bootstrap = await EnsureCatalogProviderAsync(
                provider,
                allowProviderDownloads: true,
                cancellationToken)
            .ConfigureAwait(false);

        if (bootstrap.Succeeded)
        {
            progress.Report($"{displayName} runtime registered successfully.");
            return new WinMlCatalogEpInstallResult(Succeeded: true);
        }

        string failureDetail = string.IsNullOrWhiteSpace(bootstrap.Detail)
            ? $"{displayName} catalog registration did not complete."
            : bootstrap.Detail;

        progress.Report($"{displayName} runtime registration failed: {failureDetail}");
        return new WinMlCatalogEpInstallResult(Succeeded: false, FailureDetail: failureDetail);
    }

    private static Task<WinMlCatalogBootstrapResult> EnsureCatalogProviderAsync(
        ExecutionProviderKind provider,
        bool allowProviderDownloads,
        CancellationToken cancellationToken) =>
        provider switch
        {
            ExecutionProviderKind.OpenVinoCatalog => WindowsMlCatalogEpRegistration.EnsureRegisteredAsync(
                OpenVinoCatalogProviderConstants.OrtExecutionProviderName,
                OpenVinoCatalogProviderIds.WinMl,
                WindowsIntelCatalogHardwareGate.Evaluate,
                OpenVinoCatalogOrtProbe.IsProviderListed,
                allowProviderDownloads,
                cancellationToken),
            ExecutionProviderKind.Qnn => WindowsMlCatalogEpRegistration.EnsureRegisteredAsync(
                QnnProviderConstants.OrtExecutionProviderName,
                QnnProviderIds.WinMl,
                WindowsQualcommCatalogHardwareGate.Evaluate,
                QnnOrtProbe.IsProviderListed,
                allowProviderDownloads,
                cancellationToken),
            ExecutionProviderKind.VitisAi => WindowsMlCatalogEpRegistration.EnsureRegisteredAsync(
                VitisAiProviderConstants.OrtExecutionProviderName,
                VitisAiProviderIds.WinMl,
                WindowsAmdNpuCatalogHardwareGate.Evaluate,
                VitisAiOrtProbe.IsProviderListed,
                allowProviderDownloads,
                cancellationToken),
            _ => throw new ArgumentOutOfRangeException(
                nameof(provider),
                provider,
                "Unsupported WinML catalog execution provider.")
        };
#endif

    internal static Task<WinMlCatalogEpInstallResult> Unsupported(
        IProgress<string> progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(progress);
        cancellationToken.ThrowIfCancellationRequested();

        const string failureDetail = "WinML catalog EP installation is only available in the Windows-specific build.";
        progress.Report(failureDetail);
        return Task.FromResult(new WinMlCatalogEpInstallResult(Succeeded: false, FailureDetail: failureDetail));
    }
}
