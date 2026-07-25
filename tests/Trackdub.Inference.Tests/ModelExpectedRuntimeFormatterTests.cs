using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Tests;

public sealed class ModelExpectedRuntimeFormatterTests
{
    [Theory]
    [InlineData(ModelExpectedRuntime.OrtGenAi, "ONNX Runtime GenAI")]
    [InlineData(ModelExpectedRuntime.OnnxDnnl, "Intel oneDNN (CPU)")]
    [InlineData(ModelExpectedRuntime.WindowsMlCatalogOrMigraphxOrDirectMl, "Windows ML catalog, MIGraphX, or DirectML fallback")]
    [InlineData(ModelExpectedRuntime.OnnxDirectMlOrMigraphx, "Windows ML catalog, MIGraphX, or DirectML fallback")]
    [InlineData(ModelExpectedRuntime.OnnxCudaOrMigraphx, "CUDA or MIGraphX")]
    public void FormatHint_MapsKnownManifestTokens(string token, string expectedFragment)
    {
        string? hint = ModelExpectedRuntimeFormatter.FormatHint(token);

        Assert.NotNull(hint);
        Assert.StartsWith("Runtime:", hint, StringComparison.Ordinal);
        Assert.Contains(expectedFragment, hint, StringComparison.Ordinal);
    }

    [Fact]
    public void FormatHint_ReturnsNull_WhenTokenMissing()
    {
        Assert.Null(ModelExpectedRuntimeFormatter.FormatHint(null));
        Assert.Null(ModelExpectedRuntimeFormatter.FormatHint("   "));
        Assert.Null(ModelExpectedRuntimeFormatter.FormatHint("|  |"));
    }
}
