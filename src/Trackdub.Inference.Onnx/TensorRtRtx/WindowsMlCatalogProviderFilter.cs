using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Catalog providers Trackdub must not bulk-register. The Windows ML catalog's own TRT-RTX EP
/// (Microsoft.WinML.NVIDIA.TRT-RTX.EP.*) is a distinct device spelled "NvTensorRtRtxExecutionProvider",
/// casing apart from the bundled plugin's "NvTensorRTRTXExecutionProvider"; the comparison must stay
/// OrdinalIgnoreCase. ADR-0002 point 2 routes TRT RTX only through the pinned standalone plugin.
/// </summary>
internal static class WindowsMlCatalogProviderFilter
{
    public static bool IsExcludedFromBulkRegistration(string? providerName) =>
        string.Equals(
            providerName,
            TensorRtRtxProviderConstants.PluginOrtExecutionProviderName,
            StringComparison.OrdinalIgnoreCase);
}
