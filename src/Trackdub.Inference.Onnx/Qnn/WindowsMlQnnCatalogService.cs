#if WINDOWS
using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.WinMlCatalog;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.Qnn;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsMlQnnCatalogService
{
    public Task<WinMlCatalogBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken) =>
        WindowsMlCatalogEpRegistration.EnsureRegisteredAsync(
            QnnProviderConstants.OrtExecutionProviderName,
            QnnProviderIds.WinMl,
            WindowsQualcommCatalogHardwareGate.Evaluate,
            QnnOrtProbe.IsProviderListed,
            allowProviderDownloads,
            cancellationToken);
}
#endif
