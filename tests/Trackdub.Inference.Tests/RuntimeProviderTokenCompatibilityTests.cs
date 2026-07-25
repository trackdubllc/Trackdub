using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class RuntimeProviderTokenCompatibilityTests
{
    [Theory]
    [InlineData("cpu", ExecutionProviderKind.Cpu)]
    [InlineData("dnnl", ExecutionProviderKind.Dnnl)]
    [InlineData("onednn", ExecutionProviderKind.Dnnl)]
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
    [InlineData("onnxruntime-dnnl", ExecutionProviderKind.Dnnl)]
    public void TryParseProviderToken_accepts_manifest_aliases(string token, ExecutionProviderKind expected)
    {
        Assert.True(RuntimeProviderTokenCompatibility.TryParseProviderToken(token, out ExecutionProviderKind actual));
        Assert.Equal(expected, actual);
    }

    [Theory]
    [InlineData("")]
    [InlineData("auto")]
    [InlineData("warp-drive")]
    public void TryParseProviderToken_rejects_unknown_or_non_variant_tokens(string token)
    {
        Assert.False(RuntimeProviderTokenCompatibility.TryParseProviderToken(token, out _));
    }

    [Fact]
    public void IsVariantSupportedForProvider_matches_aliases()
    {
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["dml"], ExecutionProviderKind.DirectMl));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["dnnl"], ExecutionProviderKind.Dnnl));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["onednn"], ExecutionProviderKind.Dnnl));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["tensorrt-rtx"], ExecutionProviderKind.TensorRTRtx));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["rocm"], ExecutionProviderKind.Migraphx));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["openvino-catalog"], ExecutionProviderKind.OpenVinoCatalog));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["openvino"], ExecutionProviderKind.OpenVino));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["openvino"], ExecutionProviderKind.OpenVinoCatalog));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["qnn"], ExecutionProviderKind.Qnn));
        Assert.True(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["vitisai"], ExecutionProviderKind.VitisAi));
        Assert.False(RuntimeProviderTokenCompatibility.IsVariantSupportedForProvider(["cpu"], ExecutionProviderKind.DirectMl));
    }

    [Theory]
    [InlineData("onnxruntime-dnnl", ExecutionProviderKind.Dnnl, true)]
    [InlineData("onnxruntime-dnnl", ExecutionProviderKind.Cpu, false)]
    [InlineData("onnxruntime-cpu", ExecutionProviderKind.Dnnl, false)]
    public void IsExpectedRuntimeCompatible_uses_canonical_dnnl_manifest_token(
        string expectedRuntime,
        ExecutionProviderKind provider,
        bool expected)
    {
        Assert.Equal(
            expected,
            RuntimeProviderTokenCompatibility.IsExpectedRuntimeCompatible(expectedRuntime, provider));
    }

    [Fact]
    public void ToManifestToken_uses_canonical_dnnl_expected_runtime_token()
    {
        Assert.Equal("onnxruntime-dnnl", RuntimeProviderTokenCompatibility.ToManifestToken(ExecutionProviderKind.Dnnl));
    }

    [Fact]
    public void Milestone5PlanningPolicy_listsCatalogProvidersInProbeOrder()
    {
        IReadOnlyList<ExecutionProviderKind> providers = Milestone5PlanningPolicy.SupportedProvidersThisMilestone;

        Assert.Equal(
        [
            ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderKind.Migraphx,
            ExecutionProviderKind.OpenVinoCatalog,
            ExecutionProviderKind.Qnn,
            ExecutionProviderKind.VitisAi,
            ExecutionProviderKind.TensorRt,
            ExecutionProviderKind.Cuda,
            ExecutionProviderKind.OpenVino,
            ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.Dnnl,
            ExecutionProviderKind.Cpu
        ],
            providers);
    }
}
