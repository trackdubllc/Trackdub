using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Inference.Onnx.DeepFilterNet;

internal static class DeepFilterNetSignalProcessor
{
    internal const int SampleRate = 48000;
    internal const int HopSize = 480;
    internal const int FftSize = 960;
    internal const int FreqBins = FftSize / 2 + 1;   // 481
    internal const int ErbBands = 32;
    internal const int DfOrder = 5;
    internal const int NbDf = 96;
    internal const int MinErbBinsPerBand = 2;

    // libDF norm alpha for sr=48000, hop=480, tau=1s: round(exp(-hop/(sr*tau)), 3) = 0.99.
    private const float Alpha = 0.99f;
    private const float Eps = 1e-10f;
    private const float ErbDbEps = 1e-10f;
    private const float ErbNormDivisor = 40f;

    // Hann window and the libDF-style rectangular ERB band widths are computed once.
    private static readonly float[] HannWindow = BuildHannWindow();
    internal static readonly int[] ErbBandWidths = BuildErbBandWidths();

    // Compute model features and the synthesis spectrum from mono 48 kHz PCM.
    // The exported DeepFilterNet3 contract (enc.onnx):
    //   feat_erb  [1,1,T,32] — per-band ERB energy in dB, exponential-mean normalized, /40
    //   feat_spec [1,2,T,96] — unit-normalized complex spectrum of the first NbDf bins
    //   stft      [T,481]    — raw complex spectrum kept for synthesis
    // The norm state is mutated in place so chunked callers keep libDF's running statistics.
    // Deviation from libDF noted for honesty: analysis/synthesis use a Hann window pair with
    // window-square overlap-add normalization (libDF uses a sqrt-Hann pair); the FFT is
    // unscaled on the forward side to keep feature magnitudes in the trained range.
    internal static void ComputeFeatures(
        ReadOnlySpan<float> pcm,
        DeepFilterNetFeatureNormState normState,
        out float[,,,] featErb,
        out float[,,,] featSpec,
        out Complex32[,] stft)
    {
        ArgumentNullException.ThrowIfNull(normState);

        int numFrames = pcm.Length == 0 ? 1 : (pcm.Length + HopSize - 1) / HopSize;

        var spec = new Complex32[numFrames, FreqBins];
        var featErbOut = new float[1, 1, numFrames, ErbBands];
        var featSpecOut = new float[1, 2, numFrames, NbDf];
        float[] erbMeanDb = normState.ErbMeanDb;
        float[] specUnitNorm = normState.SpecUnitNorm;
        var frame = new Complex32[FftSize];

        for (int s = 0; s < numFrames; s++)
        {
            int offset = s * HopSize;
            for (int i = 0; i < FftSize; i++)
            {
                float sample = (offset + i < pcm.Length) ? pcm[offset + i] : 0f;
                frame[i] = new Complex32(sample * HannWindow[i], 0f);
            }

            // Matlab option: unscaled forward transform (matches libDF's realfft usage);
            // InverseStft pairs it with the matching inverse option.
            Fourier.Forward(frame, FourierOptions.Matlab);

            for (int k = 0; k < FreqBins; k++)
            {
                spec[s, k] = frame[k];
            }

            // feat_spec: exponential unit-norm of the first NbDf complex bins
            // (libDF band_unit_norm: state ← α·state + (1-α)·|X|; out = X / sqrt(state)).
            for (int k = 0; k < NbDf; k++)
            {
                float magnitude = frame[k].Magnitude;
                specUnitNorm[k] = Alpha * specUnitNorm[k] + (1f - Alpha) * magnitude;
                float denom = MathF.Sqrt(MathF.Max(specUnitNorm[k], Eps));
                featSpecOut[0, 0, s, k] = frame[k].Real / denom;
                featSpecOut[0, 1, s, k] = frame[k].Imaginary / denom;
            }

            // feat_erb: mean band power in dB, exponential-mean subtracted, divided by 40
            // (libDF band_mean_norm_erb: state ← α·state + (1-α)·x; out = (x - state) / 40).
            int binStart = 0;
            for (int b = 0; b < ErbBands; b++)
            {
                int width = ErbBandWidths[b];
                float power = 0f;
                for (int k = binStart; k < binStart + width; k++)
                {
                    float mag = frame[k].Magnitude;
                    power += mag * mag;
                }

                power /= width;
                float db = 10f * MathF.Log10(power + ErbDbEps);
                erbMeanDb[b] = Alpha * erbMeanDb[b] + (1f - Alpha) * db;
                featErbOut[0, 0, s, b] = (db - erbMeanDb[b]) / ErbNormDivisor;
                binStart += width;
            }
        }

        featErb = featErbOut;
        featSpec = featSpecOut;
        stft = spec;
    }

    internal static float[] BuildLinearRamp(int length, bool rising)
    {
        var ramp = new float[length];
        if (length == 0)
        {
            return ramp;
        }

        for (int i = 0; i < length; i++)
        {
            ramp[i] = rising
                ? (float)i / length
                : (float)(length - i) / length;
        }

        return ramp;
    }

    // Reorder the raw df_dec output into the synthesis layout.
    // df_dec emits coefs [1, T, NbDf, DfOrder*2] (per bin: order-major real/imag pairs,
    // the pre-reshape layout of upstream DfOutputReshapeMF). Synthesis indexes
    // [1, T, DfOrder, NbDf, 2] with tap index 0 applying to the oldest frame.
    internal static float[,,,,] UnpackDfCoefs(ReadOnlySpan<float> rawCoefs, int numFrames)
    {
        int expected = numFrames * NbDf * DfOrder * 2;
        if (rawCoefs.Length != expected)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet df_dec returned {rawCoefs.Length} coefficient values; expected {expected} " +
                $"({numFrames} frames x {NbDf} bins x {DfOrder} taps x 2).");
        }

        var coefs = new float[1, numFrames, DfOrder, NbDf, 2];
        for (int s = 0; s < numFrames; s++)
        {
            for (int k = 0; k < NbDf; k++)
            {
                int baseIndex = ((s * NbDf) + k) * DfOrder * 2;
                for (int o = 0; o < DfOrder; o++)
                {
                    coefs[0, s, o, k, 0] = rawCoefs[baseIndex + (o * 2)];
                    coefs[0, s, o, k, 1] = rawCoefs[baseIndex + (o * 2) + 1];
                }
            }
        }

        return coefs;
    }

    // Apply model outputs to the spectrum and reconstruct PCM via ISTFT, following the
    // upstream DfNet composition: the ERB mask multiplies the raw spectrum everywhere, but
    // the first NbDf bins of the output are REPLACED by the deep filter applied to the RAW
    // (unmasked) spectrum. Tap index o applies to frame s - (DfOrder - 1) + o, i.e. the
    // last tap weights the current frame (df_lookahead = 0).
    //   erbGains [1,1,T,32]            — sigmoid mask from erb_dec
    //   dfCoefs  [1,T,DfOrder,NbDf,2]  — complex FIR taps from df_dec (see UnpackDfCoefs)
    internal static float[] Synthesize(
        Complex32[,] stftFrames,
        float[,,,] erbGains,
        float[,,,,] dfCoefs,
        int originalLength)
    {
        int numFrames = stftFrames.GetLength(0);
        var outSpec = new Complex32[numFrames, FreqBins];

        // ERB-masked path: piecewise-constant band gains over the libDF band partition.
        for (int s = 0; s < numFrames; s++)
        {
            int binStart = 0;
            for (int b = 0; b < ErbBands; b++)
            {
                int width = ErbBandWidths[b];
                float gain = erbGains[0, 0, s, b];
                for (int k = binStart; k < binStart + width; k++)
                {
                    outSpec[s, k] = new Complex32(
                        stftFrames[s, k].Real * gain,
                        stftFrames[s, k].Imaginary * gain);
                }

                binStart += width;
            }
        }

        // Deep-filter path replaces the low bins, computed from the raw spectrum.
        for (int s = 0; s < numFrames; s++)
        {
            for (int k = 0; k < NbDf; k++)
            {
                Complex32 filtered = Complex32.Zero;
                for (int o = 0; o < DfOrder; o++)
                {
                    int srcFrame = s - (DfOrder - 1) + o;
                    if (srcFrame < 0)
                    {
                        continue;
                    }

                    Complex32 src = stftFrames[srcFrame, k];
                    float cr = dfCoefs[0, s, o, k, 0];
                    float ci = dfCoefs[0, s, o, k, 1];
                    filtered += new Complex32(
                        (cr * src.Real) - (ci * src.Imaginary),
                        (cr * src.Imaginary) + (ci * src.Real));
                }

                outSpec[s, k] = filtered;
            }
        }

        return InverseStft(outSpec, originalLength);
    }

    private static float[] InverseStft(Complex32[,] frames, int originalLength)
    {
        int numFrames = frames.GetLength(0);
        int outputLength = (numFrames - 1) * HopSize + FftSize;
        var output = new float[outputLength];
        var windowSum = new float[outputLength];
        var frame = new Complex32[FftSize];

        for (int s = 0; s < numFrames; s++)
        {
            // Build full conjugate-symmetric FFT buffer from positive-frequency bins.
            frame[0] = frames[s, 0];
            for (int k = 1; k < FreqBins - 1; k++)
            {
                frame[k] = frames[s, k];
                frame[FftSize - k] = new Complex32(frames[s, k].Real, -frames[s, k].Imaginary);
            }
            frame[FreqBins - 1] = frames[s, FreqBins - 1];

            // Matlab option pairs with the unscaled forward transform in ComputeFeatures.
            Fourier.Inverse(frame, FourierOptions.Matlab);

            int offset = s * HopSize;
            for (int i = 0; i < FftSize; i++)
            {
                float w = HannWindow[i];
                output[offset + i] += frame[i].Real * w;
                windowSum[offset + i] += w * w;
            }
        }

        for (int i = 0; i < outputLength; i++)
        {
            if (windowSum[i] > Eps)
            {
                output[i] /= windowSum[i];
            }
        }

        int clampedLength = Math.Min(originalLength, outputLength);
        return output[..clampedLength];
    }

    private static float[] BuildHannWindow()
    {
        var window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            window[i] = 0.5f * (1f - MathF.Cos(2f * MathF.PI * i / FftSize));
        }
        return window;
    }

    // libDF erb_fb: contiguous rectangular bands on the Glasberg & Moore ERB scale.
    // Each band spans a whole number of FFT bins (at least MinErbBinsPerBand); the widths
    // sum to FreqBins so every bin belongs to exactly one band.
    private static int[] BuildErbBandWidths()
    {
        const float erbLQ = 24.7f * 9.265f;
        float freqWidth = (float)SampleRate / FftSize;
        float erbLow = Freq2Erb(0f);
        float erbHigh = Freq2Erb(SampleRate / 2f);
        float step = (erbHigh - erbLow) / ErbBands;

        var widths = new int[ErbBands];
        int prevBin = 0;
        int overcommitted = 0;
        for (int b = 1; b <= ErbBands; b++)
        {
            float f = Erb2Freq(erbLow + (step * b));
            int binIndex = (int)MathF.Round(f / freqWidth);
            int binCount = binIndex - prevBin - overcommitted;
            if (binCount < MinErbBinsPerBand)
            {
                overcommitted = MinErbBinsPerBand - binCount;
                binCount = MinErbBinsPerBand;
            }
            else
            {
                overcommitted = 0;
            }

            widths[b - 1] = binCount;
            prevBin = binIndex;
        }

        // Absorb rounding and include the Nyquist bin so the partition covers all FreqBins.
        int total = 0;
        foreach (int width in widths)
        {
            total += width;
        }

        widths[ErbBands - 1] += FreqBins - total;
        return widths;

        static float Freq2Erb(float f) => 9.265f * MathF.Log(1f + f / erbLQ);
        static float Erb2Freq(float e) => erbLQ * (MathF.Exp(e / 9.265f) - 1f);
    }
}
