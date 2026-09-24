using Trackdub.Inference.Runtime.TensorRtRtx;

namespace Trackdub.Inference.Onnx.TensorRtRtx;

/// <summary>
/// Catalog providers Trackdub must not bulk-register. The Windows ML catalog ships its own
/// TensorRT-RTX EP (Microsoft.WinML.NVIDIA.TRT-RTX.EP.*) under the same ORT registration name as the
/// standalone EP ABI plugin Trackdub bundles (ADR-0002). ORT allows one library per name, so letting
/// the catalog claim it first makes the pinned plugin fail with "library is already registered".
/// </summary>
internal static class WindowsMlCatalogProviderFilter
{
    public static bool IsExcludedFromBulkRegistration(string? providerName) =>
        string.Equals(
            providerName,
            TensorRtRtxProviderConstants.PluginOrtExecutionProviderName,
            StringComparison.OrdinalIgnoreCase);
}
