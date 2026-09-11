using Trackdub.Domain;

namespace Trackdub.Domain.Tests;

public sealed class ExecutionProviderTokensTests
{
    [Theory]
    [InlineData("cpu", ExecutionProviderKind.Cpu)]
    [InlineData("dnnl", ExecutionProviderKind.Dnnl)]
    [InlineData("onednn", ExecutionProviderKind.Dnnl)]
    [InlineData("onnxruntime-dnnl", ExecutionProviderKind.Dnnl)]
    [InlineData("dml", ExecutionProviderKind.DirectMl)]
    [InlineData("directml", ExecutionProviderKind.DirectMl)]
    [InlineData("cuda", ExecutionProviderKind.Cuda)]
    [InlineData("tensorrt", ExecutionProviderKind.TensorRt)]
    [InlineData("trt-rtx", ExecutionProviderKind.TensorRTRtx)]
    [InlineData("tensorrt-rtx", ExecutionProviderKind.TensorRTRtx)]
    [InlineData("migraphx", ExecutionProviderKind.Migraphx)]
    [InlineData("rocm", ExecutionProviderKind.Migraphx)]
    [InlineData("openvino", ExecutionProviderKind.OpenVino)]
    [InlineData("openvino-catalog", ExecutionProviderKind.OpenVinoCatalog)]
    [InlineData("qnn", ExecutionProviderKind.Qnn)]
    [InlineData("vitisai", ExecutionProviderKind.VitisAi)]
    [InlineData("coreml", ExecutionProviderKind.CoreMl)]
    public void TryParse_accepts_canonical_and_alias_tokens(string token, ExecutionProviderKind expected)
    {
        Assert.True(ExecutionProviderTokens.TryParse(token, out ExecutionProviderKind actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("warp-drive")]
    public void TryParse_rejects_auto_and_unknown(string token)
    {
        Assert.False(ExecutionProviderTokens.TryParse(token, out _));
    }

    [Fact]
    public void TryParseCli_auto_and_empty_yield_null_kind()
    {
        Assert.True(ExecutionProviderTokens.TryParseCli("auto", out ExecutionProviderKind? autoKind));
        Assert.Null(autoKind);
        Assert.True(ExecutionProviderTokens.TryParseCli(null, out ExecutionProviderKind? emptyKind));
        Assert.Null(emptyKind);
    }

    [Fact]
    public void ToCanonicalTag_uses_cli_primary_tags()
    {
        Assert.Equal("dnnl", ExecutionProviderTokens.ToCanonicalTag(ExecutionProviderKind.Dnnl));
        Assert.Equal("trt-rtx", ExecutionProviderTokens.ToCanonicalTag(ExecutionProviderKind.TensorRTRtx));
        Assert.Equal("coreml", ExecutionProviderTokens.ToCanonicalTag(ExecutionProviderKind.CoreMl));
        Assert.Equal("openvino-catalog", ExecutionProviderTokens.ToCanonicalTag(ExecutionProviderKind.OpenVinoCatalog));
    }

    [Fact]
    public void ToManifestToken_keeps_dnnl_expected_runtime_spelling()
    {
        Assert.Equal("onnxruntime-dnnl", ExecutionProviderTokens.ToManifestToken(ExecutionProviderKind.Dnnl));
        Assert.Equal("trt-rtx", ExecutionProviderTokens.ToManifestToken(ExecutionProviderKind.TensorRTRtx));
    }

    [Fact]
    public void CliTags_include_auto_and_coreml()
    {
        Assert.Contains("auto", ExecutionProviderTokens.CliTags);
        Assert.Contains("coreml", ExecutionProviderTokens.CliTags);
        Assert.Contains("trt-rtx", ExecutionProviderTokens.CliTags);
        Assert.DoesNotContain("max-performance", ExecutionProviderTokens.CliTags);
    }

    [Fact]
    public void ResolvePlatformPin_cuda_maps_to_trt_rtx_on_windows()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.Equal(ExecutionProviderKind.Cuda, ExecutionProviderTokens.ResolvePlatformPin(ExecutionProviderKind.Cuda));
            return;
        }

        Assert.Equal(
            ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderTokens.ResolvePlatformPin(ExecutionProviderKind.Cuda));
    }

    [Fact]
    public void ResolvePlatformPin_cuda_stays_cuda_on_linux()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        Assert.Equal(ExecutionProviderKind.Cuda, ExecutionProviderTokens.ResolvePlatformPin(ExecutionProviderKind.Cuda));
    }

    [Fact]
    public void PlatformPinsEquivalent_treats_windows_cuda_and_trt_rtx_as_same_pin()
    {
        if (!OperatingSystem.IsWindows())
        {
            Assert.False(ExecutionProviderTokens.PlatformPinsEquivalent(
                ExecutionProviderKind.Cuda,
                ExecutionProviderKind.TensorRTRtx));
            return;
        }

        Assert.True(ExecutionProviderTokens.PlatformPinsEquivalent(
            ExecutionProviderKind.Cuda,
            ExecutionProviderKind.TensorRTRtx));
    }
}
