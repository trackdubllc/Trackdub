using Trackdub.Inference.Onnx.DeepFilterNet;

namespace Trackdub.Inference.Tests.DeepFilterNet;

public sealed class DeepFilterNetSignalProcessorTests
{
    [Fact]
    public void ComputeFeatures_ProducesModelContractShapes()
    {
        int numSamples = DeepFilterNetSignalProcessor.HopSize * 10;
        float[] pcm = BuildSine(numSamples, frequencyHz: 440f);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            pcm,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out float[,,,] featErb,
            out float[,,,] featSpec,
            out MathNet.Numerics.Complex32[,] stft);

        int expectedFrames = (numSamples + DeepFilterNetSignalProcessor.HopSize - 1)
            / DeepFilterNetSignalProcessor.HopSize;

        Assert.Equal(1, featErb.GetLength(0));
        Assert.Equal(1, featErb.GetLength(1));
        Assert.Equal(expectedFrames, featErb.GetLength(2));
        Assert.Equal(DeepFilterNetSignalProcessor.ErbBands, featErb.GetLength(3));

        // Regression for P2-10: enc.onnx expects feat_spec [1,2,T,96] (the deep-filter bins),
        // not the full 481-bin one-sided spectrum.
        Assert.Equal(1, featSpec.GetLength(0));
        Assert.Equal(2, featSpec.GetLength(1));
        Assert.Equal(expectedFrames, featSpec.GetLength(2));
        Assert.Equal(DeepFilterNetSignalProcessor.NbDf, featSpec.GetLength(3));

        Assert.Equal(expectedFrames, stft.GetLength(0));
        Assert.Equal(DeepFilterNetSignalProcessor.FreqBins, stft.GetLength(1));
    }

    [Fact]
    public void ComputeFeatures_NormStateCarry_ChangesFirstFrameFromFreshStart()
    {
        float[] pcm = BuildSine(DeepFilterNetSignalProcessor.HopSize * 8, frequencyHz: 440f);

        DeepFilterNetFeatureNormState warmedState = DeepFilterNetFeatureNormState.CreateInitial();
        DeepFilterNetSignalProcessor.ComputeFeatures(pcm, warmedState, out _, out _, out _);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            pcm,
            warmedState,
            out float[,,,] featErbCarried,
            out _,
            out _);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            pcm,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out float[,,,] featErbFresh,
            out _,
            out _);

        Assert.NotEqual(featErbFresh[0, 0, 0, 0], featErbCarried[0, 0, 0, 0]);
    }

    [Fact]
    public void FeatureNormState_InitialValues_MatchLibDfRamps()
    {
        DeepFilterNetFeatureNormState state = DeepFilterNetFeatureNormState.CreateInitial();

        Assert.Equal(DeepFilterNetSignalProcessor.ErbBands, state.ErbMeanDb.Length);
        Assert.Equal(DeepFilterNetSignalProcessor.NbDf, state.SpecUnitNorm.Length);

        // libDF MEAN_NORM_INIT = [-60, -90] and UNIT_NORM_INIT = [0.001, 0.0001] linear ramps.
        Assert.Equal(-60f, state.ErbMeanDb[0], precision: 4);
        Assert.Equal(-90f, state.ErbMeanDb[^1], precision: 4);
        Assert.Equal(0.001f, state.SpecUnitNorm[0], precision: 6);
        Assert.Equal(0.0001f, state.SpecUnitNorm[^1], precision: 6);
    }

    [Fact]
    public void ErbBandWidths_PartitionAllFrequencyBins()
    {
        int[] widths = DeepFilterNetSignalProcessor.ErbBandWidths;

        Assert.Equal(DeepFilterNetSignalProcessor.ErbBands, widths.Length);
        Assert.Equal(DeepFilterNetSignalProcessor.FreqBins, widths.Sum());
        Assert.All(widths, static width =>
            Assert.True(width >= DeepFilterNetSignalProcessor.MinErbBinsPerBand,
                $"Each ERB band must span at least {DeepFilterNetSignalProcessor.MinErbBinsPerBand} bins, got {width}."));
    }

    [Fact]
    public void Synthesize_IdentityGainsAndIdentityTap_ReconstructsSignal()
    {
        // 1 kHz sits in the deep-filter range (bin 20 < 96), so this exercises the DF path.
        int length = 4800;
        float[] sine = BuildSine(length, frequencyHz: 1000f);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            sine,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        int numFrames = stft.GetLength(0);
        float[,,,] erbGains = BuildUnityGains(numFrames);

        // Identity deep filter: the LAST tap applies to the current frame (df_lookahead = 0),
        // so tap index DfOrder - 1 set to 1 + 0i must reproduce the input.
        var dfCoefs = new float[1, numFrames, DeepFilterNetSignalProcessor.DfOrder, DeepFilterNetSignalProcessor.NbDf, 2];
        for (int s = 0; s < numFrames; s++)
        {
            for (int k = 0; k < DeepFilterNetSignalProcessor.NbDf; k++)
            {
                dfCoefs[0, s, DeepFilterNetSignalProcessor.DfOrder - 1, k, 0] = 1f;
            }
        }

        float[] reconstructed = DeepFilterNetSignalProcessor.Synthesize(stft, erbGains, dfCoefs, length);

        Assert.Equal(length, reconstructed.Length);

        // Interior samples (full window overlap, full tap history) must match closely;
        // this pins both the forward/inverse FFT scaling pair and the tap alignment.
        int start = DeepFilterNetSignalProcessor.FftSize * 2;
        int end = length - DeepFilterNetSignalProcessor.FftSize;
        for (int i = start; i < end; i++)
        {
            Assert.True(MathF.Abs(reconstructed[i] - sine[i]) < 1e-3f,
                $"Sample {i} diverged: expected {sine[i]:F6}, got {reconstructed[i]:F6}.");
        }
    }

    [Fact]
    public void Synthesize_HighFrequencyOutsideDfRange_PreservesLevelWithUnityGains()
    {
        // 10 kHz maps to bin 200, above the 96 deep-filter bins, so zero DF coefficients
        // must not affect it; unity ERB gains must preserve its level.
        int length = DeepFilterNetSignalProcessor.SampleRate / 10;
        float[] sine = BuildSine(length, frequencyHz: 10000f, amplitude: 0.5f);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            sine,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        int numFrames = stft.GetLength(0);
        float[,,,] erbGains = BuildUnityGains(numFrames);
        var dfCoefs = new float[1, numFrames, DeepFilterNetSignalProcessor.DfOrder, DeepFilterNetSignalProcessor.NbDf, 2];

        float[] reconstructed = DeepFilterNetSignalProcessor.Synthesize(stft, erbGains, dfCoefs, length);
        float rmsRatio = ComputeRms(reconstructed) / ComputeRms(sine);

        Assert.True(rmsRatio is > 0.9f and < 1.1f,
            $"RMS ratio {rmsRatio:F3} indicates broken FFT scaling or band gain application.");
    }

    [Fact]
    public void UnpackDfCoefs_MapsOrderMajorPairsPerBin()
    {
        const int numFrames = 2;
        int perFrame = DeepFilterNetSignalProcessor.NbDf * DeepFilterNetSignalProcessor.DfOrder * 2;
        var raw = new float[numFrames * perFrame];

        // Encode (t, f, o, c) into a distinct value per element using the raw df_dec layout
        // [1, T, NbDf, DfOrder*2] with order-major real/imag pairs in the last dimension.
        for (int t = 0; t < numFrames; t++)
        {
            for (int f = 0; f < DeepFilterNetSignalProcessor.NbDf; f++)
            {
                for (int o = 0; o < DeepFilterNetSignalProcessor.DfOrder; o++)
                {
                    for (int c = 0; c < 2; c++)
                    {
                        int rawIndex = (((t * DeepFilterNetSignalProcessor.NbDf) + f)
                            * DeepFilterNetSignalProcessor.DfOrder * 2) + (o * 2) + c;
                        raw[rawIndex] = Encode(t, f, o, c);
                    }
                }
            }
        }

        float[,,,,] unpacked = DeepFilterNetSignalProcessor.UnpackDfCoefs(raw, numFrames);

        for (int t = 0; t < numFrames; t++)
        {
            for (int o = 0; o < DeepFilterNetSignalProcessor.DfOrder; o++)
            {
                for (int f = 0; f < DeepFilterNetSignalProcessor.NbDf; f++)
                {
                    Assert.Equal(Encode(t, f, o, 0), unpacked[0, t, o, f, 0]);
                    Assert.Equal(Encode(t, f, o, 1), unpacked[0, t, o, f, 1]);
                }
            }
        }

        static float Encode(int t, int f, int o, int c) =>
            (t * 10000f) + (f * 100f) + (o * 10f) + c;
    }

    [Fact]
    public void UnpackDfCoefs_WrongElementCount_ThrowsActionableError()
    {
        var raw = new float[10];

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(
            () => DeepFilterNetSignalProcessor.UnpackDfCoefs(raw, numFrames: 2));

        Assert.Contains("df_dec", ex.Message, StringComparison.Ordinal);
        Assert.Contains("expected", ex.Message, StringComparison.Ordinal);
    }

    private static float[,,,] BuildUnityGains(int numFrames)
    {
        var erbGains = new float[1, 1, numFrames, DeepFilterNetSignalProcessor.ErbBands];
        for (int s = 0; s < numFrames; s++)
        {
            for (int b = 0; b < DeepFilterNetSignalProcessor.ErbBands; b++)
            {
                erbGains[0, 0, s, b] = 1f;
            }
        }

        return erbGains;
    }

    private static float[] BuildSine(int length, float frequencyHz, float amplitude = 1f)
    {
        var pcm = new float[length];
        for (int i = 0; i < length; i++)
        {
            pcm[i] = amplitude * MathF.Sin(2f * MathF.PI * frequencyHz * i / DeepFilterNetSignalProcessor.SampleRate);
        }

        return pcm;
    }

    private static float ComputeRms(float[] samples)
    {
        float sum = 0f;
        foreach (float s in samples)
        {
            sum += s * s;
        }
        return MathF.Sqrt(sum / samples.Length);
    }
}
