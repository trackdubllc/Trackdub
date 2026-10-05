using MathNet.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Inference.Onnx.DeepFilterNet;

internal static class DeepFilterNetSignalProcessor
{
    internal const int SampleRate = 48000;
    internal const int HopSize = 480;
    internal const int FftSize = 960;
    internal const int FreqBins = (FftSize / 2) + 1;   // 481
    internal const int ErbBands = 32;
    internal const int DfOrder = 5;
    internal const int NbDf = 96;
    internal const int MinErbBinsPerBand = 2;

    // DeepFilterNet3 trains with conv_lookahead = df_lookahead = 2. The ONNX export leaves the
    // lookahead to the caller; the streaming engine reads the extra hops past each window.
    internal const int ConvLookahead = 2;
    internal const int DfLookahead = 2;

    // libDF scales the analysis spectrum by wnorm = 1 / (fft_size^2 / (2 * hop)); the model's
    // feat_spec unit norm is not scale invariant, so features must see the same scale.
    private const float WindowNorm = 1f / (FftSize * FftSize / (2f * HopSize));

    // libDF norm alpha for sr=48000, hop=480, tau=1s: round(exp(-hop/(sr*tau)), 3) = 0.99.
    private const float Alpha = 0.99f;
    private const float Eps = 1e-10f;
    private const float ErbDbEps = 1e-10f;
    private const float ErbNormDivisor = 40f;

    // libDF vorbis window and the libDF-style rectangular ERB band widths are computed once.
    private static readonly float[] AnalysisWindow = BuildVorbisWindow();
    internal static readonly int[] ErbBandWidths = BuildErbBandWidths();

    /// <summary>
    /// Computes model features with native libDF clock semantics (matching the reference
    /// <c>deep-filter -D</c> render):
    /// <list type="bullet">
    /// <item>Frame <c>s</c> analyzes the window over samples <c>[(s-1)*hop, (s+1)*hop)</c> — the
    /// previous hop plus the current hop, with zero history at the start (out-of-range reads are
    /// zeros, so the first frame is the zero-history hop).</item>
    /// <item>Feature rows are NOT shifted: row <c>s</c> is the feature of frame <c>s</c> (the ONNX
    /// export leaves lookahead padding to the caller, which feeds lookback rows itself).</item>
    /// </list>
    /// The norm state is mutated in place; each frame's features advance it exactly once.
    /// </summary>
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
            // Native analysis origin: window starts one hop before frame s (zero history at s=0).
            int offset = (s - 1) * HopSize;
            for (int i = 0; i < FftSize; i++)
            {
                int index = offset + i;
                float sample = (index >= 0 && index < pcm.Length) ? pcm[index] : 0f;
                frame[i] = new Complex32(sample * AnalysisWindow[i], 0f);
            }

            // Matlab option: unscaled forward transform, then libDF's wnorm;
            // synthesis undoes wnorm before the matching inverse option.
            Fourier.Forward(frame, FourierOptions.Matlab);

            for (int k = 0; k < FreqBins; k++)
            {
                frame[k] *= WindowNorm;
                spec[s, k] = frame[k];
            }

            // feat_spec: exponential unit-norm of the first NbDf complex bins
            // (libDF band_unit_norm: state ← α·state + (1-α)·|X|; out = X / sqrt(state)).
            for (int k = 0; k < NbDf; k++)
            {
                float magnitude = frame[k].Magnitude;
                specUnitNorm[k] = (Alpha * specUnitNorm[k]) + ((1f - Alpha) * magnitude);
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
                erbMeanDb[b] = (Alpha * erbMeanDb[b]) + ((1f - Alpha) * db);
                featErbOut[0, 0, s, b] = (db - erbMeanDb[b]) / ErbNormDivisor;

                binStart += width;
            }
        }

        featErb = featErbOut;
        featSpec = featSpecOut;
        stft = spec;
    }

    /// <summary>
    /// Per-frame feature computation for the streaming engine: analyzes one 960-sample window
    /// (previous hop + current hop), advances the norm state once, and returns the spectrum plus
    /// the ERB and deep-filter feature rows.
    /// </summary>
    internal static void ComputeFrameFeatures(
        ReadOnlySpan<float> analysisWindow,
        DeepFilterNetFeatureNormState normState,
        out Complex32[] spectrum,
        out float[] erbRow,
        out float[] specReal,
        out float[] specImag)
    {
        if (analysisWindow.Length != FftSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(analysisWindow), $"Analysis window must be {FftSize} samples, got {analysisWindow.Length}.");
        }

        ArgumentNullException.ThrowIfNull(normState);

        var frame = new Complex32[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            frame[i] = new Complex32(analysisWindow[i] * AnalysisWindow[i], 0f);
        }

        Fourier.Forward(frame, FourierOptions.Matlab);

        spectrum = new Complex32[FreqBins];
        for (int k = 0; k < FreqBins; k++)
        {
            frame[k] *= WindowNorm;
            spectrum[k] = frame[k];
        }

        float[] erbMeanDb = normState.ErbMeanDb;
        float[] specUnitNorm = normState.SpecUnitNorm;
        erbRow = new float[ErbBands];
        specReal = new float[NbDf];
        specImag = new float[NbDf];

        for (int k = 0; k < NbDf; k++)
        {
            float magnitude = spectrum[k].Magnitude;
            specUnitNorm[k] = (Alpha * specUnitNorm[k]) + ((1f - Alpha) * magnitude);
            float denom = MathF.Sqrt(MathF.Max(specUnitNorm[k], Eps));
            specReal[k] = spectrum[k].Real / denom;
            specImag[k] = spectrum[k].Imaginary / denom;
        }

        int binStart = 0;
        for (int b = 0; b < ErbBands; b++)
        {
            int width = ErbBandWidths[b];
            float power = 0f;
            for (int k = binStart; k < binStart + width; k++)
            {
                float mag = spectrum[k].Magnitude;
                power += mag * mag;
            }

            power /= width;
            float db = 10f * MathF.Log10(power + ErbDbEps);
            erbMeanDb[b] = (Alpha * erbMeanDb[b]) + ((1f - Alpha) * db);
            erbRow[b] = (db - erbMeanDb[b]) / ErbNormDivisor;

            binStart += width;
        }
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

    /// <summary>
    /// Expands a per-ERB-band gain vector [ErbBands] to per-bin gains [FreqBins] using the
    /// libDF rectangular band partition (piecewise constant within each band).
    /// </summary>
    internal static float[] ExpandErbGains(ReadOnlySpan<float> erbGains)
    {
        if (erbGains.Length != ErbBands)
        {
            throw new ArgumentOutOfRangeException(
                nameof(erbGains), $"Expected {ErbBands} ERB gains, got {erbGains.Length}.");
        }

        var gains = new float[FreqBins];
        int binStart = 0;
        for (int b = 0; b < ErbBands; b++)
        {
            int width = ErbBandWidths[b];
            for (int k = binStart; k < binStart + width; k++)
            {
                gains[k] = erbGains[b];
            }

            binStart += width;
        }

        return gains;
    }

    /// <summary>
    /// Native libDF synthesis for a window of W frames:
    /// <list type="bullet">
    /// <item><paramref name="stftFrames"/> holds W+4 raw spectra: indices 0..3 are the lookback
    /// frames (w0-4..w0-1, zeros for the first window), indices 4..W+3 are the window frames.</item>
    /// <item>The output for window frame j applies mask/coefs of frame j to the DELAYED spectrum
    /// (frame j-2, i.e. stft index j+2), the deep-filter FIR spans raw spectra at taps -4..0
    /// (stft indices j..j+4), and the attenuation-limit mix uses the delayed noisy spectrum —
    /// all matching libDF's rolling-spectrum clock.</item>
    /// <item>The inverse FFT is windowed and returned per frame: element [j*FftSize, (j+1)*FftSize)
    /// is frame j's time-domain contribution. The caller overlaps each frame's second half with the
    /// next frame's first half (hop synthesis with a carry, as libDF's synthesis_mem does).</item>
    /// </list>
    /// </summary>
    /// <param name="stftFrames">W+4 raw spectra; window frame j is at index j+4.</param>
    /// <param name="erbGains">[1,1,W,32] per-window-frame ERB masks (already gated to unity or zeros).</param>
    /// <param name="dfCoefs">[1,W,DfOrder,NbDf,2] per-window-frame FIR taps (already gated).</param>
    /// <param name="attenuationLimit">Linear mix-back limit (0 disables; libDF 10^(-db/20)).</param>
    /// <param name="applyDf">Whether the deep-filter stage ran for each frame; when false, the masked delayed spectrum is retained.</param>
    /// <returns>W * FftSize floats: per-frame windowed inverse FFTs, concatenated.</returns>
    internal static float[] Synthesize(
        Complex32[,] stftFrames,
        float[,,,] erbGains,
        float[,,,,] dfCoefs,
        float attenuationLimit = 0f,
        bool[]? applyDf = null)
    {
        int totalFrames = stftFrames.GetLength(0);
        int windowFrames = totalFrames - 4;
        if (windowFrames < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(stftFrames), $"Synthesis needs at least 5 spectrum frames (4 lookback + 1), got {totalFrames}.");
        }

        if (erbGains.GetLength(2) != windowFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(erbGains), $"Expected {windowFrames} gain frames, got {erbGains.GetLength(2)}.");
        }

        if (dfCoefs.GetLength(1) != windowFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(dfCoefs), $"Expected {windowFrames} coefficient frames, got {dfCoefs.GetLength(1)}.");
        }

        if (applyDf is not null && applyDf.Length != windowFrames)
        {
            throw new ArgumentOutOfRangeException(
                nameof(applyDf), $"Expected {windowFrames} deep-filter decisions, got {applyDf.Length}.");
        }

        var output = new float[windowFrames * FftSize];
        var frame = new Complex32[FftSize];

        for (int j = 0; j < windowFrames; j++)
        {
            float[] binGains = ExpandErbGains(GetGainRow(erbGains, j));
            var outSpec = new Complex32[FreqBins];
            for (int k = 0; k < FreqBins; k++)
            {
                Complex32 d = stftFrames[j + 2, k];
                outSpec[k] = new Complex32(d.Real * binGains[k], d.Imaginary * binGains[k]);
            }

            // Deep-filter path replaces the low bins, computed from the raw spectra at taps -4..0.
            // A null decision array preserves the standalone synthesis behavior used by callers
            // that provide coefficients for every frame.
            if (applyDf is null || applyDf[j])
            {
                for (int k = 0; k < NbDf; k++)
                {
                    Complex32 filtered = Complex32.Zero;
                    for (int o = 0; o < DfOrder; o++)
                    {
                        Complex32 src = stftFrames[j + o, k];
                        float cr = dfCoefs[0, j, o, k, 0];
                        float ci = dfCoefs[0, j, o, k, 1];
                        filtered += new Complex32(
                            (cr * src.Real) - (ci * src.Imaginary),
                            (cr * src.Imaginary) + (ci * src.Real));
                    }

                    outSpec[k] = filtered;
                }
            }

            if (attenuationLimit > 0f)
            {
                float keep = 1f - attenuationLimit;
                for (int k = 0; k < FreqBins; k++)
                {
                    Complex32 d = stftFrames[j + 2, k];
                    outSpec[k] = (d * attenuationLimit) + (outSpec[k] * keep);
                }
            }

            // Build the full conjugate-symmetric buffer and invert (Matlab option, undoing wnorm).
            frame[0] = outSpec[0];
            for (int k = 1; k < FreqBins - 1; k++)
            {
                frame[k] = outSpec[k];
                frame[FftSize - k] = new Complex32(outSpec[k].Real, -outSpec[k].Imaginary);
            }

            frame[FreqBins - 1] = outSpec[FreqBins - 1];
            Fourier.Inverse(frame, FourierOptions.Matlab);
            float unscale = 1f / WindowNorm;

            int baseIndex = j * FftSize;
            for (int i = 0; i < FftSize; i++)
            {
                output[baseIndex + i] = frame[i].Real * unscale * AnalysisWindow[i];
            }
        }

        return output;
    }

    private static float[] GetGainRow(float[,,,] erbGains, int frame)
    {
        var row = new float[ErbBands];
        for (int b = 0; b < ErbBands; b++)
        {
            row[b] = erbGains[0, 0, frame, b];
        }

        return row;
    }

    // libDF vorbis window: sin(pi/2 * sin^2(pi * (n + 0.5) / N)).
    private static float[] BuildVorbisWindow()
    {
        var window = new float[FftSize];
        for (int i = 0; i < FftSize; i++)
        {
            float inner = MathF.Sin(MathF.PI * (i + 0.5f) / FftSize);
            window[i] = MathF.Sin(0.5f * MathF.PI * inner * inner);
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

        static float Freq2Erb(float f) => 9.265f * MathF.Log(1f + (f / erbLQ));
        static float Erb2Freq(float e) => erbLQ * (MathF.Exp(e / 9.265f) - 1f);
    }
}