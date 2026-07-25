namespace Trackdub.Inference.Onnx.WindowsMl;

/// <summary>
/// Windows ML ExecutionProviderCatalog provider names for certified catalog EPs.
/// </summary>
/// <remarks>
/// #TODO(winml-catalog-ep): Confirm ids against ExecutionProviderCatalog / Microsoft docs for the target Windows ML SDK
/// before enabling discovery, registration, or session append. See docs/internal/windows-ml-phase-5-catalog-eps.md.
/// </remarks>
internal static class WindowsMlCatalogProviderIds
{
    internal const string OpenVinoCatalog = "OpenVINOExecutionProvider";

    internal const string Qnn = "QNNExecutionProvider";

    internal const string VitisAi = "VitisAIExecutionProvider";
}
