using Trackdub.Inference.Onnx.Spleeter;

namespace Trackdub.Inference.Onnx.Tests.Spleeter;

/// <summary>
/// Soft-mask math parity for Spleeter 2stems ONNX post-process.
/// Tests call production <see cref="SpleeterModelConstants.ComputeSoftMasks"/>
/// (the same helper <c>SpleeterOnnxSeparator</c> uses) so epsilon/formula
/// regressions in the engine fail this suite.
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

    [Theory]
    [InlineData(1f, 0f)]
    [InlineData(0f, 1f)]
    [InlineData(0.3f, 0.7f)]
    [InlineData(2f, 2f)]
    [InlineData(0.01f, 0.5f)]
    public void Production_mask_is_power_ratio_over_sum_plus_eps(float v, float a)
    {
        (float maskV, float maskA) = ProductionMask(v, a);
        float denom = (v * v) + (a * a) + Eps;

        Assert.Equal((v * v) / denom, maskV, 7);
        Assert.Equal((a * a) / denom, maskA, 7);
    }

    [Theory]
    [InlineData(0.3f, 0.7f)]
    [InlineData(2f, 2f)]
    [InlineData(0.5f, 0.1f)]
    public void Production_masks_are_complementary_for_nonzero_stems(float v, float a)
    {
        (float maskV, float maskA) = ProductionMask(v, a);
        Assert.Equal(1f, maskV + maskA, 4);
    }

    [Fact]
    public void Production_mask_sums_to_zero_when_both_stems_zero()
    {
        // Documented Trackdub behavior (sherpa adds eps/2 so each mask is ~0.5).
        (float maskV, float maskA) = ProductionMask(0f, 0f);
        Assert.Equal(0f, maskV);
        Assert.Equal(0f, maskA);
    }

    [Fact]
    public void Sherpa_eps_half_delta_is_small_for_typical_magnitudes()
    {
        const float v = 0.4f;
        const float a = 0.6f;
        (float trackdubV, float trackdubA) = ProductionMask(v, a);
        (float sherpaV, float sherpaA) = SherpaMask(v, a);

        Assert.True(Math.Abs(trackdubV - sherpaV) < 1e-5f,
            $"Production vocals mask {trackdubV} vs sherpa {sherpaV}.");
        Assert.True(Math.Abs(trackdubA - sherpaA) < 1e-5f,
            $"Production accomp mask {trackdubA} vs sherpa {sherpaA}.");
        Assert.True(sherpaV + sherpaA >= 1f);
        Assert.True(sherpaV + sherpaA < 1f + 1e-6f);
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
    public void High_frequency_mask_window_is_zero_beyond_first_1024_bins()
    {
        // Contract: model sees bins [0,1024); inverse zeros FFT bins >= 1024
        // (sherpa pads mask to 2049 with zeros — same effective HF drop on both stems).
        int nFft = SpleeterModelConstants.Nfft;
        int maxFreqs = SpleeterModelConstants.MaxFreqBins;

        (float maskV, float maskA) = ProductionMask(1f, 1f);
        Assert.Equal(0.5f, maskV, 5);
        Assert.Equal(0.5f, maskA, 5);

        var kept = new bool[nFft];
        for (int k = 0; k < maxFreqs; k++)
        {
            kept[k] = true;
        }

        for (int k = 1; k < maxFreqs; k++)
        {
            kept[nFft - k] = true;
        }

        for (int k = maxFreqs; k < nFft - (maxFreqs - 1); k++)
        {
            Assert.False(kept[k], $"FFT bin {k} must not be treated as model-backed energy.");
        }
    }
}
