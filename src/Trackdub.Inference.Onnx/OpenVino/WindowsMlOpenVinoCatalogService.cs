#if WINDOWS
using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.WinMlCatalog;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.OpenVino;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsMlOpenVinoCatalogService
{
    public Task<WinMlCatalogBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken) =>
        WindowsMlCatalogEpRegistration.EnsureRegisteredAsync(
            OpenVinoCatalogProviderConstants.OrtExecutionProviderName,
            OpenVinoCatalogProviderIds.WinMl,
            WindowsIntelCatalogHardwareGate.Evaluate,
            OpenVinoCatalogOrtProbe.IsProviderListed,
            allowProviderDownloads,
            cancellationToken);
}
#endif
