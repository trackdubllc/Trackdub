#if WINDOWS
using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Onnx.WinMlCatalog;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.VitisAi;

[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowsMlVitisAiCatalogService
{
    public Task<WinMlCatalogBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken) =>
        WindowsMlCatalogEpRegistration.EnsureRegisteredAsync(
            VitisAiProviderConstants.OrtExecutionProviderName,
            VitisAiProviderIds.WinMl,
            WindowsAmdNpuCatalogHardwareGate.Evaluate,
            VitisAiOrtProbe.IsProviderListed,
            allowProviderDownloads,
            cancellationToken);
}
#endif
