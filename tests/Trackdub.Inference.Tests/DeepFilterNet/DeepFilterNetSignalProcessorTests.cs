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
    public void ComputeFeatures_NativeOrigin_NoFeatureShiftAndZeroHistoryFirstFrame()
    {
        // Silence then a tone: with the native libDF origin, frame s analyzes samples
        // [(s-1)*hop, (s+1)*hop) and feature row s is the feature of frame s (no shift).
        int hop = DeepFilterNetSignalProcessor.HopSize;
        float[] pcm = new float[hop * 20];
        float[] tone = BuildSine(hop * 10, frequencyHz: 1000f);
        tone.CopyTo(pcm, hop * 10);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            pcm,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out float[,,,] featSpec,
            out MathNet.Numerics.Complex32[,] stft);

        int firstStftFrame = FirstFrame(t => stft[t, 20].Magnitude > 1e-3f, stft.GetLength(0));
        int firstFeatRow = FirstFrame(
            t => MathF.Abs(featSpec[0, 0, t, 20]) + MathF.Abs(featSpec[0, 1, t, 20]) > 1f,
            featSpec.GetLength(2));

        // Tone starts at sample 10*hop; the first analysis window containing it is frame 10
        // (window [(10-1)*hop, (10+1)*hop) = [4320, 5280) covers sample 4800).
        Assert.Equal(10, firstStftFrame);
        Assert.Equal(10, firstFeatRow);

        // No shift means no zero feature tail: the last row is populated for a real frame.
        Assert.True(MathF.Abs(featSpec[0, 0, featSpec.GetLength(2) - 1, 20]) +
                    MathF.Abs(featSpec[0, 1, featSpec.GetLength(2) - 1, 20]) > 0f);
    }

    [Fact]
    public void ComputeFeatures_NativeOrigin_FirstFrameUsesZeroHistoryWindow()
    {
        // A pure tone: frame 0's window is [zeros(hop), pcm[0:hop]) — the zero-history half must
        // make frame 0's spectrum differ from frame 1's (window [pcm[0:hop], pcm[hop:2*hop])).
        int hop = DeepFilterNetSignalProcessor.HopSize;
        float[] pcm = BuildSine(hop * 4, frequencyHz: 1000f);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            pcm,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        Assert.NotEqual(stft[0, 20].Magnitude, stft[1, 20].Magnitude);
    }

    [Fact]
    public void ExpandErbGains_PiecewiseConstantOverBandPartition()
    {
        float[] gains = DeepFilterNetSignalProcessor.ExpandErbGains(
            Enumerable.Repeat(0.5f, DeepFilterNetSignalProcessor.ErbBands).ToArray());

        Assert.Equal(DeepFilterNetSignalProcessor.FreqBins, gains.Length);
        Assert.All(gains, static g => Assert.Equal(0.5f, g));
    }

    [Fact]
    public void Synthesize_IdentityTap_ReconstructsCurrentFrameExactly()
    {
        // 1060 Hz is not an exact FFT bin (bin 21.2) and its 45.28-sample period shares no
        // divisor with the 480-sample hop, so unlike a 1 kHz tone this test is sensitive to
        // both reconstruction error and hop-delay mistakes: a whole-hop delay would read as
        // an O(1) mismatch, not a rounding error.
        // Gains are zero so only the DF path contributes: with the identity tap at index
        // DfOrder-1 the low bins must reproduce the CURRENT frame's spectrum exactly.
        int length = 4800;
        float[] sine = BuildSine(length, frequencyHz: 1060f);
        float[] padded = PrependHopZeros(sine, 4); // 4 zero lookback rows ahead of the signal

        DeepFilterNetSignalProcessor.ComputeFeatures(
            padded,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        int totalFrames = stft.GetLength(0);          // 4 lookback + 10 signal frames
        int windowFrames = totalFrames - 4;
        var erbGains = new float[1, 1, windowFrames, DeepFilterNetSignalProcessor.ErbBands];

        // Identity deep filter: offset 0 sits at tap index DfOrder-1 (taps span offsets -4..0).
        int currentTap = DeepFilterNetSignalProcessor.DfOrder - 1;
        var dfCoefs = new float[1, windowFrames, DeepFilterNetSignalProcessor.DfOrder, DeepFilterNetSignalProcessor.NbDf, 2];
        for (int s = 0; s < windowFrames; s++)
        {
            for (int k = 0; k < DeepFilterNetSignalProcessor.NbDf; k++)
            {
                dfCoefs[0, s, currentTap, k, 0] = 1f;
            }
        }

        float[] synthesized = DeepFilterNetSignalProcessor.Synthesize(stft, erbGains, dfCoefs);

        // Per-frame td contributions; hop j = td[j][0:480] + td[j-1][480:960].
        Assert.Equal(windowFrames * DeepFilterNetSignalProcessor.FftSize, synthesized.Length);
        float[] output = OverlapHops(synthesized, windowFrames);

        // The DF current tap reproduces the current window frame, whose analysis window
        // covers the PREVIOUS hop plus the current one; hop synthesis keeps the first
        // half, so the identity reconstruction appears at a one-hop delay.
        int hop = DeepFilterNetSignalProcessor.HopSize;
        int start = 2 * hop;
        int end = Math.Min(output.Length, length) - hop;
        for (int i = start; i < end; i++)
        {
            int expectedIndex = i - hop;
            float expected = expectedIndex >= 0 && expectedIndex < length ? sine[expectedIndex] : 0f;
            Assert.True(MathF.Abs(output[i] - expected) < 1e-3f,
                $"Sample {i} diverged: expected {expected:F6}, got {output[i]:F6}.");
        }
    }

    [Fact]
    public void Synthesize_HighFrequencyOutsideDfRange_PreservesLevelWithUnityGains()
    {
        // 10 kHz maps to bin 200, above the 96 deep-filter bins, so zero DF coefficients
        // must not affect it; unity ERB gains must preserve its level (the native clock delays
        // the output, which leaves RMS unchanged).
        int length = DeepFilterNetSignalProcessor.SampleRate / 10;
        float[] sine = BuildSine(length, frequencyHz: 10000f, amplitude: 0.5f);
        float[] padded = PrependHopZeros(sine, 4);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            padded,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        int windowFrames = stft.GetLength(0) - 4;
        float[,,,] erbGains = BuildUnityGains(windowFrames);
        var dfCoefs = new float[1, windowFrames, DeepFilterNetSignalProcessor.DfOrder, DeepFilterNetSignalProcessor.NbDf, 2];

        float[] synthesized = DeepFilterNetSignalProcessor.Synthesize(stft, erbGains, dfCoefs);
        float[] output = OverlapHops(synthesized, windowFrames);

        // The unity path carries the DELAYED spectrum, so the signal onset (from the zero
        // lookback) flushes through the first hops; the trailing carry is dropped. Compare
        // RMS over the steady interior only.
        int hop = DeepFilterNetSignalProcessor.HopSize;
        float[] outputSteady = output[(4 * hop)..(output.Length - hop)];
        float[] sineSteady = sine[(4 * hop)..(sine.Length - hop)];
        float rmsRatio = ComputeRms(outputSteady) / ComputeRms(sineSteady);

        Assert.True(rmsRatio is > 0.9f and < 1.1f,
            $"RMS ratio {rmsRatio:F3} indicates broken FFT scaling or band gain application.");
    }

    [Fact]
    public void Synthesize_DeepFilterSkipped_PreservesLowBandWithUnityGains()
    {
        // 1 kHz sits inside the deep-filter bins. With DF skipped (Clean / GainsOnly gates) the
        // unity-masked delayed spectrum must pass through; applying zero taps would silence it.
        int length = DeepFilterNetSignalProcessor.SampleRate / 10;
        float[] sine = BuildSine(length, frequencyHz: 1000f, amplitude: 0.5f);
        float[] padded = PrependHopZeros(sine, 4);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            padded,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        int windowFrames = stft.GetLength(0) - 4;
        float[,,,] erbGains = BuildUnityGains(windowFrames);
        var dfCoefs = new float[1, windowFrames, DeepFilterNetSignalProcessor.DfOrder, DeepFilterNetSignalProcessor.NbDf, 2];

        float[] withDf = DeepFilterNetSignalProcessor.Synthesize(stft, erbGains, dfCoefs);
        float[] skipDf = DeepFilterNetSignalProcessor.Synthesize(
            stft, erbGains, dfCoefs, applyDeepFilter: new bool[windowFrames]);

        int hop = DeepFilterNetSignalProcessor.HopSize;
        float[] withDfSteady = OverlapHops(withDf, windowFrames)[(4 * hop)..^hop];
        float[] skipSteady = OverlapHops(skipDf, windowFrames)[(4 * hop)..^hop];
        float sineRms = ComputeRms(sine[(4 * hop)..(sine.Length - hop)]);

        Assert.True(ComputeRms(withDfSteady) < 0.01f * sineRms, "Zero taps should silence the DF band.");
        float ratio = ComputeRms(skipSteady) / sineRms;
        Assert.True(ratio is > 0.9f and < 1.1f, $"Skipped-DF RMS ratio {ratio:F3} lost the low band.");
    }

    [Fact]
    public void Synthesize_FullAttenuationLimit_ReturnsDelayedSourceDespiteZeroMask()
    {
        // attenuationLimit = 1 mixes back the full delayed noisy spectrum, so a zero mask and
        // zero taps must still reproduce the source (delayed by one hop, native clock).
        int length = 4800;
        float[] sine = BuildSine(length, frequencyHz: 1000f);
        float[] padded = PrependHopZeros(sine, 4);

        DeepFilterNetSignalProcessor.ComputeFeatures(
            padded,
            DeepFilterNetFeatureNormState.CreateInitial(),
            out _,
            out _,
            out MathNet.Numerics.Complex32[,] stft);

        int windowFrames = stft.GetLength(0) - 4;
        float[] synthesized = DeepFilterNetSignalProcessor.Synthesize(
            stft,
            new float[1, 1, windowFrames, DeepFilterNetSignalProcessor.ErbBands],
            new float[1, windowFrames, DeepFilterNetSignalProcessor.DfOrder, DeepFilterNetSignalProcessor.NbDf, 2],
            attenuationLimit: 1f);
        float[] output = OverlapHops(synthesized, windowFrames);

        int start = DeepFilterNetSignalProcessor.FftSize;
        int end = Math.Min(output.Length, length) - DeepFilterNetSignalProcessor.FftSize;
        for (int i = start; i < end; i++)
        {
            // The native clock applies the attenuation mix to the DELAYED spectrum (two
            // frames back) and hop synthesis adds one more hop of delay, so a full mix-back
            // reproduces the source shifted by 3 * HopSize samples — the system delay the
            // native -D compensation trims (LatencyTrimSamples).
            int expectedIndex = i - (3 * DeepFilterNetSignalProcessor.HopSize);
            float expected = expectedIndex >= 0 && expectedIndex < length ? sine[expectedIndex] : 0f;
            Assert.True(MathF.Abs(output[i] - expected) < 1e-3f,
                $"Sample {i} diverged: expected {expected:F6}, got {output[i]:F6}.");
        }
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

    private static float[] PrependHopZeros(float[] pcm, int hopCount)
    {
        var padded = new float[pcm.Length + (hopCount * DeepFilterNetSignalProcessor.HopSize)];
        pcm.CopyTo(padded, hopCount * DeepFilterNetSignalProcessor.HopSize);
        return padded;
    }

    private static float[] OverlapHops(float[] synthesized, int windowFrames)
    {
        // Hop synthesis: hop j = td[j][0:480] + carry, carry = td[j][480:960].
        var output = new float[windowFrames * DeepFilterNetSignalProcessor.HopSize];
        int hop = DeepFilterNetSignalProcessor.HopSize;
        int fft = DeepFilterNetSignalProcessor.FftSize;
        var carry = new float[hop];
        for (int j = 0; j < windowFrames; j++)
        {
            for (int i = 0; i < hop; i++)
            {
                output[(j * hop) + i] = synthesized[(j * fft) + i] + carry[i];
            }

            for (int i = 0; i < hop; i++)
            {
                carry[i] = synthesized[(j * fft) + hop + i];
            }
        }

        return output;
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

    private static int FirstFrame(Func<int, bool> predicate, int count)
    {
        for (int t = 0; t < count; t++)
        {
            if (predicate(t))
            {
                return t;
            }
        }

        return -1;
    }
}
