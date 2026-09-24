using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class WindowsMlCatalogProviderFilterTests
{
    [Theory]
    // The catalog's own TRT-RTX EP would claim the name the bundled EP ABI plugin registers under.
    [InlineData("NvTensorRTRTXExecutionProvider", true)]
    [InlineData("NvTensorRtRtxExecutionProvider", true)]
    [InlineData("DmlExecutionProvider", false)]
    [InlineData("QNNExecutionProvider", false)]
    [InlineData("OpenVINOExecutionProvider", false)]
    [InlineData("VitisAIExecutionProvider", false)]
    [InlineData("MIGraphXExecutionProvider", false)]
    [InlineData(null, false)]
    public void Only_the_trt_rtx_catalog_provider_is_excluded_from_bulk_registration(string? providerName, bool expected)
    {
        Assert.Equal(expected, WindowsMlCatalogProviderFilter.IsExcludedFromBulkRegistration(providerName));
    }
}
