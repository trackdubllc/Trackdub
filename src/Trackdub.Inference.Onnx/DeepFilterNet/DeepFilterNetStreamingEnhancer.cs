using MathNet.Numerics;
using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Inference.Onnx.DeepFilterNet;

/// <summary>
/// Native libDF decoder-stage decision for one frame (thresholds match the
/// <c>deep-filter</c> CLI defaults: min -15 dB, max ERB/DF 35 dB).
/// </summary>
internal enum DeepFilterNetFrameGate
{
    /// <summary>Local SNR below the minimum: zero mask, no deep filter (noise only).</summary>
    NoiseOnly,

    /// <summary>Local SNR above the DF maximum but below the ERB maximum: mask only.</summary>
    GainsOnly,

    /// <summary>Regular noisy speech: mask and deep filter.</summary>
    Speech,

    /// <summary>Local SNR above the ERB maximum: no processing.</summary>
    Clean,
}

/// <summary>
/// State-preserving DeepFilterNet3 enhancement with native libDF clock semantics.
/// Features, normalization statistics, GRU hidden states, convolution lookbacks and the
/// synthesis overlap carry advance chronologically over active (non-quiet) frames; bounded
/// inference windows are a pure throughput choice and never restart state. Quiet hops emit
/// zeros with the entire state clock frozen, matching native's pre-analysis early return.
/// Output is one hop per input hop minus the 3-hop (1440-sample) native <c>-D</c> system
/// delay, so the result is 30 ms shorter than the input and time-aligned with it.
/// </summary>
internal static class DeepFilterNetStreamingEnhancer
{
    internal const int DefaultWindowFrames = 600;

    /// <summary>Native CLI decoder thresholds (dB).</summary>
    internal const float MinDbThresh = -15f;
    internal const float MaxDbErbThresh = 35f;
    internal const float MaxDbDfThresh = 35f;

    /// <summary>Native quiet-hop gate: mean-square below this freezes the whole state clock.</summary>
    internal const float QuietMeanSquareThresh = 1e-7f;

    internal const int MaxQuietHopsProcessed = 5;

    /// <summary>Native <c>-D</c> output latency trim: FFT size minus hop plus two hops.</summary>
    internal const int LatencyTrimSamples = 1440;

    // Trackdub's PCM reader normalizes by 32768; native divides by 32767. The quiet gate
    // threshold is scale-sensitive, so match native's scale before gating and featuring.
    private const float NativeInputScale = 32768f / 32767f;


    /// <summary>
    /// Classifies one frame's decoder stages from its encoder local-SNR estimate, mirroring
    /// native <c>apply_stages</c> (strict comparisons: equality takes full processing).
    /// </summary>
    internal static DeepFilterNetFrameGate ClassifyGate(float lsnr)
    {
        if (lsnr < MinDbThresh)
        {
            return DeepFilterNetFrameGate.NoiseOnly;
        }

        if (lsnr > MaxDbErbThresh)
        {
            return DeepFilterNetFrameGate.Clean;
        }

        if (lsnr > MaxDbDfThresh)
        {
            return DeepFilterNetFrameGate.GainsOnly;
        }

        return DeepFilterNetFrameGate.Speech;
    }

    internal static long GetOutputSampleCount(long inputSamples)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(inputSamples);
        int hop = DeepFilterNetSignalProcessor.HopSize;
        long hops = (inputSamples / hop) + (inputSamples % hop == 0 ? 0 : 1);
        return Math.Max(0, checked(hops * hop) - LatencyTrimSamples);
    }

    /// <summary>
    /// Streams chronological PCM blocks to a sink with backpressure. All input, feature and
    /// output buffers are bounded by windowFrames, including arbitrarily long quiet runs.
    /// The sink must consume each block before returning; memory is reused afterwards.
    /// </summary>
    public static async Task<long> EnhanceAsync(
        IAudioSamples audio,
        DeepFilterNetModelSessions sessions,
        float attenuationLimit,
        Func<ReadOnlyMemory<float>, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken,
        int windowFrames = DefaultWindowFrames)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(writeAsync);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(windowFrames, 0);
        long totalSamples = audio.SampleFrameCount;
        _ = GetOutputSampleCount(totalSamples);
        cancellationToken.ThrowIfCancellationRequested();
        if (totalSamples == 0)
        {
            return 0;
        }

        ArgumentNullException.ThrowIfNull(sessions);
        return await EnhanceCoreAsync(audio, sessions, attenuationLimit, windowFrames,
            totalSamples, writeAsync, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<long> EnhanceCoreAsync(
        IAudioSamples audio,
        DeepFilterNetModelSessions sessions,
        float attenuationLimit,
        int windowFrames,
        long totalSamples,
        Func<ReadOnlyMemory<float>, CancellationToken, ValueTask> writeAsync,
        CancellationToken cancellationToken)
    {
        int hop = DeepFilterNetSignalProcessor.HopSize;
        int fft = DeepFilterNetSignalProcessor.FftSize;
        int erbBands = DeepFilterNetSignalProcessor.ErbBands;
        int nbDf = DeepFilterNetSignalProcessor.NbDf;
        int dfOrder = DeepFilterNetSignalProcessor.DfOrder;
        int c0FrameSize = 64 * nbDf;

        // Physical windows cap latency and memory even if the whole clip is silent.
        int windowSamples = checked(windowFrames * hop);
        var pcmWindow = new float[windowSamples];
        var hopOut = new float[windowSamples];
        var ordinalToHop = new int[windowFrames];

        var normState = DeepFilterNetFeatureNormState.CreateInitial();
        var recurrentState = new DeepFilterNetRecurrentState();

        // Lookback caches (native rolling history): 2 feature rows, 4 spectra, and the last
        // 4 c0 rows from DF-executed frames (zeros until the first DF run).
        var featErbCache = new float[2, erbBands];
        var featSpecRealCache = new float[2, nbDf];
        var featSpecImagCache = new float[2, nbDf];
        var specCache = new Complex32[DeepFilterNetOnnxInference.DfC0LookbackRows, DeepFilterNetSignalProcessor.FreqBins];
        var c0Cache = new float[DeepFilterNetOnnxInference.DfC0LookbackRows * c0FrameSize];

        var synthesisMemory = new float[hop];
        var analysisWindow = new float[fft];

        int quietRun = 0;
        long inputOffset = 0;
        long samplesWritten = 0;
        int trimRemaining = LatencyTrimSamples;
        while (inputOffset < totalSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int readSamples = (int)Math.Min(windowSamples, totalSamples - inputOffset);
            int hopCount = (readSamples + hop - 1) / hop;
            int outputSamples = hopCount * hop;
            Array.Clear(pcmWindow);
            Array.Clear(hopOut);
            audio.ReadMonoSamples(inputOffset, pcmWindow.AsSpan(0, readSamples));
            inputOffset += readSamples;

            int windowActive = 0;
            for (int h = 0; h < hopCount; h++)
            {
                double sumSquares = 0;
                for (int i = h * hop; i < (h + 1) * hop; i++)
                {
                    pcmWindow[i] *= NativeInputScale;
                    sumSquares += (double)pcmWindow[i] * pcmWindow[i];
                }

                // Native skip_counter: only the 6th consecutive quiet hop onwards is dropped.
                quietRun = sumSquares / hop >= QuietMeanSquareThresh ? 0 : quietRun + 1;
                if (quietRun <= MaxQuietHopsProcessed)
                {
                    ordinalToHop[windowActive++] = h;
                }
            }

            if (windowActive > 0)
            {
                // Chronological features for the window's active frames; the norm state advances
                // exactly once per active frame and is never replayed over overlaps.
                var specNew = new Complex32[windowActive, DeepFilterNetSignalProcessor.FreqBins];
                var erbNew = new float[windowActive, erbBands];
                var specRealNew = new float[windowActive, nbDf];
                var specImagNew = new float[windowActive, nbDf];
                for (int j = 0; j < windowActive; j++)
                {
                    // The preceding active hop survives quiet gaps and window boundaries.
                    Array.Copy(analysisWindow, hop, analysisWindow, 0, hop);
                    Array.Copy(pcmWindow, ordinalToHop[j] * hop, analysisWindow, hop, hop);
                    DeepFilterNetSignalProcessor.ComputeFrameFeatures(
                        analysisWindow, normState,
                        out Complex32[] spectrum, out float[] erbRow, out float[] specReal, out float[] specImag);
                    for (int k = 0; k < DeepFilterNetSignalProcessor.FreqBins; k++)
                    {
                        specNew[j, k] = spectrum[k];
                    }

                    for (int b = 0; b < erbBands; b++)
                    {
                        erbNew[j, b] = erbRow[b];
                    }

                    for (int k = 0; k < nbDf; k++)
                    {
                        specRealNew[j, k] = specReal[k];
                        specImagNew[j, k] = specImag[k];
                    }
                }

                DeepFilterNetEncoderResult encoder = DeepFilterNetOnnxInference.RunEncoderWindow(
                    sessions,
                    BuildEncoderFeed(featErbCache, erbNew, windowActive, erbBands),
                    BuildEncoderFeedComplex(featSpecRealCache, featSpecImagCache, specRealNew, specImagNew, windowActive, nbDf),
                    recurrentState,
                    cancellationToken);

                // Refresh feature lookbacks with the window's last two rows.
                RefreshFeatureCache(featErbCache, erbNew, windowActive, erbBands);
                RefreshFeatureCache(featSpecRealCache, specRealNew, windowActive, nbDf);
                RefreshFeatureCache(featSpecImagCache, specImagNew, windowActive, nbDf);

                // Per-frame gate decisions; consecutive equal decisions form runs so decoder
                // GRU states advance through exactly the frames native would execute.
                var gainsWindow = new float[1, 1, windowActive, erbBands];
                var coefsWindow = new float[1, windowActive, dfOrder, nbDf, 2];
                var applyDeepFilter = new bool[windowActive];
                int runStart = 0;
                while (runStart < windowActive)
                {
                    DeepFilterNetFrameGate gate = ClassifyGate(encoder.Lsnr[runStart]);
                    int runEnd = runStart + 1;
                    while (runEnd < windowActive && ClassifyGate(encoder.Lsnr[runEnd]) == gate)
                    {
                        runEnd++;
                    }

                    RunDecoderRun(
                        sessions, encoder, c0Cache, recurrentState,
                        gainsWindow, coefsWindow, runStart, runEnd - runStart, gate, cancellationToken);

                    if (gate == DeepFilterNetFrameGate.Speech)
                    {
                        Array.Fill(applyDeepFilter, true, runStart, runEnd - runStart);
                    }

                    if (gate is DeepFilterNetFrameGate.Speech or DeepFilterNetFrameGate.GainsOnly)
                    {
                        RefreshC0Cache(c0Cache, encoder.C0, runStart, runEnd - runStart, c0FrameSize);
                    }

                    runStart = runEnd;
                }

                float[] timeDomain = DeepFilterNetSignalProcessor.Synthesize(
                    BuildSpectrumWindow(specCache, specNew, windowActive),
                    gainsWindow,
                    coefsWindow,
                    attenuationLimit,
                    applyDeepFilter);

                // Hop overlap-add with a carry across windows (native synthesis_mem).
                for (int j = 0; j < windowActive; j++)
                {
                    int hopIndex = ordinalToHop[j];
                    for (int i = 0; i < hop; i++)
                    {
                        hopOut[hopIndex * hop + i] = timeDomain[j * fft + i] + synthesisMemory[i];
                        synthesisMemory[i] = timeDomain[j * fft + hop + i];
                    }
                }

                RefreshSpectrumCache(specCache, specNew, windowActive);
            }

            // Trim in the physical output clock, including quiet hops and small windows.
            int skip = Math.Min(trimRemaining, outputSamples);
            trimRemaining -= skip;
            if (skip < outputSamples)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await writeAsync(hopOut.AsMemory(skip, outputSamples - skip), cancellationToken)
                    .ConfigureAwait(false);
                samplesWritten += outputSamples - skip;
            }
        }

        return samplesWritten;
    }

    private static void RunDecoderRun(
        DeepFilterNetModelSessions sessions,
        DeepFilterNetEncoderResult encoder,
        float[] c0Cache,
        DeepFilterNetRecurrentState recurrentState,
        float[,,,] gainsWindow,
        float[,,,,] coefsWindow,
        int offsetFrames,
        int runFrames,
        DeepFilterNetFrameGate gate,
        CancellationToken cancellationToken)
    {
        switch (gate)
        {
            case DeepFilterNetFrameGate.Speech:
                {
                    float[,,,] gains = DeepFilterNetOnnxInference.RunErbDecoderWindow(
                        sessions, encoder, offsetFrames, runFrames, recurrentState, cancellationToken);
                    float[,,,,] coefs = DeepFilterNetOnnxInference.RunDfDecoderWindow(
                        sessions, encoder, c0Cache, offsetFrames, runFrames, recurrentState, cancellationToken);
                    CopyGainsRun(gainsWindow, gains, offsetFrames, runFrames);
                    CopyCoefsRun(coefsWindow, coefs, offsetFrames, runFrames);
                    break;
                }

            case DeepFilterNetFrameGate.GainsOnly:
                {
                    float[,,,] gains = DeepFilterNetOnnxInference.RunErbDecoderWindow(
                        sessions, encoder, offsetFrames, runFrames, recurrentState, cancellationToken);
                    CopyGainsRun(gainsWindow, gains, offsetFrames, runFrames);
                    // coefsWindow stays zero and applyDeepFilter stays false: native skips the DF
                    // stage, so Synthesize keeps the masked delayed low bins.
                    break;
                }

            case DeepFilterNetFrameGate.NoiseOnly:
                // gainsWindow/coefsWindow stay zero: native applies a zero mask and skips DF.
                break;

            case DeepFilterNetFrameGate.Clean:
                for (int j = 0; j < runFrames; j++)
                {
                    for (int b = 0; b < DeepFilterNetSignalProcessor.ErbBands; b++)
                    {
                        gainsWindow[0, 0, offsetFrames + j, b] = 1f;
                    }
                }

                // applyDeepFilter stays false: the unity mask keeps the delayed spectrum.
                break;

            default:
                throw new ArgumentOutOfRangeException(nameof(gate), $"Unknown frame gate {gate}.");
        }
    }

    private static float[,,,] BuildEncoderFeed(float[,] cache, float[,] rows, int windowActive, int bands)
    {
        var feed = new float[1, 1, windowActive + 2, bands];
        for (int b = 0; b < bands; b++)
        {
            feed[0, 0, 0, b] = cache[0, b];
            feed[0, 0, 1, b] = cache[1, b];
            for (int j = 0; j < windowActive; j++)
            {
                feed[0, 0, j + 2, b] = rows[j, b];
            }
        }

        return feed;
    }

    private static float[,,,] BuildEncoderFeedComplex(
        float[,] realCache, float[,] imagCache, float[,] realNew, float[,] imagNew, int windowActive, int bins)
    {
        var feed = new float[1, 2, windowActive + 2, bins];
        for (int k = 0; k < bins; k++)
        {
            feed[0, 0, 0, k] = realCache[0, k];
            feed[0, 1, 0, k] = imagCache[0, k];
            feed[0, 0, 1, k] = realCache[1, k];
            feed[0, 1, 1, k] = imagCache[1, k];
            for (int j = 0; j < windowActive; j++)
            {
                feed[0, 0, j + 2, k] = realNew[j, k];
                feed[0, 1, j + 2, k] = imagNew[j, k];
            }
        }

        return feed;
    }

    private static void RefreshFeatureCache(float[,] cache, float[,] rows, int windowActive, int bands)
    {
        int last = windowActive - 1;
        int previous = windowActive - 2;
        for (int b = 0; b < bands; b++)
        {
            cache[0, b] = previous >= 0 ? rows[previous, b] : cache[1, b];
            cache[1, b] = rows[last, b];
        }
    }

    private static void RefreshC0Cache(float[] cache, float[] encoderC0, int offsetFrames, int runFrames, int frameSize)
    {
        // Keep the last 4 frame-major rows across DF-executed runs (single-copy shift; the
        // cache is 4 rows and runs are typically far longer).
        int keepRows = Math.Min(DeepFilterNetOnnxInference.DfC0LookbackRows, runFrames);
        int shiftBytes = (DeepFilterNetOnnxInference.DfC0LookbackRows - keepRows) * frameSize * sizeof(float);
        if (shiftBytes > 0)
        {
            Buffer.BlockCopy(cache, keepRows * frameSize * sizeof(float), cache, 0, shiftBytes);
        }

        Buffer.BlockCopy(
            encoderC0, ((offsetFrames + runFrames - keepRows) * frameSize) * sizeof(float),
            cache, shiftBytes,
            keepRows * frameSize * sizeof(float));
    }

    private static Complex32[,] BuildSpectrumWindow(Complex32[,] cache, Complex32[,] rows, int windowActive)
    {
        int lookback = DeepFilterNetOnnxInference.DfC0LookbackRows;
        int bins = DeepFilterNetSignalProcessor.FreqBins;
        var window = new Complex32[windowActive + lookback, bins];
        for (int r = 0; r < lookback; r++)
        {
            for (int k = 0; k < bins; k++)
            {
                window[r, k] = cache[r, k];
            }
        }

        for (int j = 0; j < windowActive; j++)
        {
            for (int k = 0; k < bins; k++)
            {
                window[j + lookback, k] = rows[j, k];
            }
        }

        return window;
    }

    private static void RefreshSpectrumCache(Complex32[,] cache, Complex32[,] rows, int windowActive)
    {
        int lookback = DeepFilterNetOnnxInference.DfC0LookbackRows;
        int bins = DeepFilterNetSignalProcessor.FreqBins;
        int keepRows = Math.Min(lookback, windowActive);
        for (int r = 0; r < lookback - keepRows; r++)
        {
            for (int k = 0; k < bins; k++)
            {
                cache[r, k] = cache[r + keepRows, k];
            }
        }

        for (int r = 0; r < keepRows; r++)
        {
            for (int k = 0; k < bins; k++)
            {
                cache[lookback - keepRows + r, k] = rows[windowActive - keepRows + r, k];
            }
        }
    }

    private static void CopyGainsRun(float[,,,] destination, float[,,,] run, int offsetFrames, int runFrames)
    {
        int bands = DeepFilterNetSignalProcessor.ErbBands;
        for (int j = 0; j < runFrames; j++)
        {
            for (int b = 0; b < bands; b++)
            {
                destination[0, 0, offsetFrames + j, b] = run[0, 0, j, b];
            }
        }
    }

    private static void CopyCoefsRun(float[,,,,] destination, float[,,,,] run, int offsetFrames, int runFrames)
    {
        int order = DeepFilterNetSignalProcessor.DfOrder;
        int bins = DeepFilterNetSignalProcessor.NbDf;
        for (int j = 0; j < runFrames; j++)
        {
            for (int o = 0; o < order; o++)
            {
                for (int k = 0; k < bins; k++)
                {
                    destination[0, offsetFrames + j, o, k, 0] = run[0, j, o, k, 0];
                    destination[0, offsetFrames + j, o, k, 1] = run[0, j, o, k, 1];
                }
            }
        }
    }
}
