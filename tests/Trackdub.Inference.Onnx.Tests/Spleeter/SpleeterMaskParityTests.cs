using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// Soft-mask math parity for Spleeter 2stems ONNX post-process.
/// Tests call production <see cref="SpleeterModelConstants.ComputeSoftMasks"/>
/// (the same helper <c>SpleeterOnnxSeparator</c> uses).
/// Formula target: sherpa-onnx separate_onnx.py
/// <c>(stem² + ε/2) / (vocals² + accomp² + ε)</c>.
/// </summary>
public sealed class SpleeterMaskParityTests
{
    private const float Eps = SpleeterModelConstants.MaskEpsilon;

    private static (float MaskV, float MaskA) ProductionMask(float v, float a)
    {
        SpleeterModelConstants.ComputeSoftMasks(v, a, out float maskV, out float maskA);
        return (maskV, maskA);
    }

    private static (float MaskV, float MaskA) SherpaMask(float v, float a)
    {
        float denom = (v * v) + (a * a) + Eps;
        float halfEps = Eps / 2f;
        return (((v * v) + halfEps) / denom, ((a * a) + halfEps) / denom);
    }

    [Fact]
    public void Constants_lock_sherpa_onnx_stft_contract_literals()
    {
        // Independent of SpleeterStftProcessor aliases — absolute parity values.
        Assert.Equal(44100, SpleeterModelConstants.TargetSampleRate);
        Assert.Equal(4096, SpleeterModelConstants.Nfft);
        Assert.Equal(1024, SpleeterModelConstants.Hop);
        Assert.Equal(1024, SpleeterModelConstants.MaxFreqBins);
        Assert.Equal(512, SpleeterModelConstants.TimePad);
        Assert.Equal(1e-10f, SpleeterModelConstants.MaskEpsilon);
        Assert.Equal("vocals.onnx", SpleeterModelConstants.VocalsModelFileName);
        Assert.Equal("accompaniment.onnx", SpleeterModelConstants.AccompanimentModelFileName);
    }

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0.3f, 0.7f)]
    [InlineData(2f, 2f)]
    [InlineData(0.01f, 0.5f)]
    public void Production_mask_matches_sherpa_power_ratio_plus_half_eps(float v, float a)
    {
        (float maskV, float maskA) = ProductionMask(v, a);
        (float sherpaV, float sherpaA) = SherpaMask(v, a);

        Assert.Equal(sherpaV, maskV, 6);
        Assert.Equal(sherpaA, maskA, 6);
    }

    [Theory]
    [InlineData(0.3f, 0.7f)]
    [InlineData(2f, 2f)]
    [InlineData(0.5f, 0.1f)]
    [InlineData(0f, 0f)]
    public void Production_masks_sum_to_one(float v, float a)
    {
        // sherpa: (v²+ε/2 + a²+ε/2) / (v²+a²+ε) = 1 exactly in real arithmetic.
        (float maskV, float maskA) = ProductionMask(v, a);
        Assert.Equal(1f, maskV + maskA, 4);
    }

    [Fact]
    public void Production_mask_equals_sherpa_reference_formula()
    {
        const float v = 0.4f;
        const float a = 0.6f;
        (float productionV, float productionA) = ProductionMask(v, a);
        (float sherpaV, float sherpaA) = SherpaMask(v, a);

        Assert.Equal(sherpaV, productionV, 7);
        Assert.Equal(sherpaA, productionA, 7);
        Assert.Equal(1f, productionV + productionA, 5);
    }

    [Fact]
    public void Dominant_stem_mask_approaches_one()
    {
        (float maskV, float maskA) = ProductionMask(10f, 0.001f);
        Assert.True(maskV > 0.999f);
        Assert.True(maskA < 0.001f);

        (float maskV2, float maskA2) = ProductionMask(0.001f, 10f);
        Assert.True(maskA2 > 0.999f);
        Assert.True(maskV2 < 0.001f);
    }

    [Fact]
    public void ResolveModelPath_joins_relative_known_file_names_under_model_root()
    {
        // Independent oracle: literal file names + directory separator.
        string root = Path.Combine("models", "spleeter");
        string sep = Path.DirectorySeparatorChar.ToString();
        string vocals = SpleeterModelConstants.ResolveModelPath(root, "vocals.onnx");
        string acc = SpleeterModelConstants.ResolveModelPath(root, "accompaniment.onnx");

        Assert.Equal(root + sep + "vocals.onnx", vocals);
        Assert.Equal(root + sep + "accompaniment.onnx", acc);
    }

    [Fact]
    public void ResolveModelPath_rejects_rooted_and_traversing_file_names()
    {
        Assert.Throws<ArgumentException>(() =>
            SpleeterModelConstants.ResolveModelPath("models/spleeter", "/abs/vocals.onnx"));
        Assert.Throws<ArgumentException>(() =>
            SpleeterModelConstants.ResolveModelPath("models/spleeter", "../outside.onnx"));
        Assert.Throws<ArgumentException>(() =>
            SpleeterModelConstants.ResolveModelPath("models/spleeter", "other.onnx"));
    }
}
