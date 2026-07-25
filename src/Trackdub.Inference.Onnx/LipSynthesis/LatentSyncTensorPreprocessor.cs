using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Inference.Onnx.LipSynthesis;

/// <summary>
/// Converts raw video frame bytes and audio PCM to/from ONNX tensor format for LatentSync.
/// All operations are pure math — no image library dependency.
/// </summary>
internal static class LatentSyncTensorPreprocessor
{
    public const int TargetHeight = 512;
    public const int TargetWidth = 512;
    public const int LatentChannels = 4;
    public const int LatentHeight = 64; // 512 / 8
    public const int LatentWidth = 64;

    // Whisper mel parameters (must match whisper/audio.py exactly)
    private const int MelBins = 80;
    private const int MelFrames = 3000; // 30 s at 100 fps hop
    private const int WinLength = 400;  // Whisper n_fft
    private const int HopLength = 160;
    private const int FreqBins = WinLength / 2 + 1; // 201
    private const int NSamples = 16000 * 30;        // 480 000

    private static readonly float[] HannWin = BuildHannWindow(WinLength);
    private static readonly float[,] MelFilters = BuildWhisperMelFilterbank();

    /// <summary>
    /// Converts raw RGBA bytes (any size) to a normalized float CHW tensor [1, 3, 512, 512] in [-1, 1].
    /// Performs nearest-neighbour resize on the way in.
    /// </summary>
    public static float[] RgbaToNormalizedTensor(ReadOnlySpan<byte> rgba, int srcWidth, int srcHeight)
    {
        float[] tensor = new float[3 * TargetHeight * TargetWidth];

        float scaleX = (float)srcWidth / TargetWidth;
        float scaleY = (float)srcHeight / TargetHeight;

        for (int y = 0; y < TargetHeight; y++)
        {
            int sy = Math.Min((int)(y * scaleY), srcHeight - 1);
            for (int x = 0; x < TargetWidth; x++)
            {
                int sx = Math.Min((int)(x * scaleX), srcWidth - 1);
                int srcIdx = (sy * srcWidth + sx) * 4; // RGBA

                float r = rgba[srcIdx] / 127.5f - 1f;
                float g = rgba[srcIdx + 1] / 127.5f - 1f;
                float b = rgba[srcIdx + 2] / 127.5f - 1f;

                int dstBase = y * TargetWidth + x;
                tensor[0 * TargetHeight * TargetWidth + dstBase] = r;
                tensor[1 * TargetHeight * TargetWidth + dstBase] = g;
                tensor[2 * TargetHeight * TargetWidth + dstBase] = b;
            }
        }

        return tensor;
    }

    /// <summary>
    /// Pastes a synthesized 512×512 RGB tensor (from VAE decode) back into a full-size RGBA frame
    /// at the given face bounding box. Allocates a new RGBA buffer of size (fullWidth × fullHeight × 4).
    /// </summary>
    public static void PasteFloatTensorIntoRgba(
        ReadOnlySpan<float> synTensor,
        Span<byte> fullFrameRgba,
        int fullWidth, int fullHeight,
        int faceX, int faceY, int faceW, int faceH)
    {
        float scaleX = (float)TargetWidth / faceW;
        float scaleY = (float)TargetHeight / faceH;

        for (int dy = 0; dy < faceH; dy++)
        {
            int sy = Math.Min((int)(dy * scaleY), TargetHeight - 1);
            for (int dx = 0; dx < faceW; dx++)
            {
                int sx = Math.Min((int)(dx * scaleX), TargetWidth - 1);

                float r = Math.Clamp((synTensor[0 * TargetHeight * TargetWidth + sy * TargetWidth + sx] + 1f) * 127.5f, 0f, 255f);
                float g = Math.Clamp((synTensor[1 * TargetHeight * TargetWidth + sy * TargetWidth + sx] + 1f) * 127.5f, 0f, 255f);
                float b = Math.Clamp((synTensor[2 * TargetHeight * TargetWidth + sy * TargetWidth + sx] + 1f) * 127.5f, 0f, 255f);

                int px = faceX + dx;
                int py = faceY + dy;
                if (px < 0 || px >= fullWidth || py < 0 || py >= fullHeight)
                {
                    continue;
                }

                int dstIdx = (py * fullWidth + px) * 4;
                fullFrameRgba[dstIdx] = (byte)r;
                fullFrameRgba[dstIdx + 1] = (byte)g;
                fullFrameRgba[dstIdx + 2] = (byte)b;
                fullFrameRgba[dstIdx + 3] = 255;
            }
        }
    }

    /// <summary>
    /// Computes a Whisper-compatible log-mel spectrogram from 16 kHz mono PCM.
    /// Returns float[<see cref="MelBins"/> * <see cref="MelFrames"/>] in row-major order (mel-bin outer).
    /// </summary>
    public static float[] ComputeWhisperMelSpectrogram(float[] pcm16000Hz)
    {
        // Pad/trim to NSamples, then pad right by WinLength so frame loop never reads past end.
        // With NSamples+WinLength total samples, frame t accesses t*HopLength .. t*HopLength+WinLength-1.
        // At t=MelFrames-1=2999: 2999*160+399 = 479839 < 480400. Safe.
        float[] padded = new float[NSamples + WinLength];
        Array.Copy(pcm16000Hz, padded, Math.Min(pcm16000Hz.Length, NSamples));

        float[,] power = ComputePowerSpectrum(padded);
        float[] mel = ApplyMelFilterbank(power);
        WhisperNormalize(mel);
        return mel;
    }

    public static (int MelBins, int MelFrames) MelShape => (MelBins, MelFrames);

    private static float[,] ComputePowerSpectrum(float[] samples)
    {
        var result = new float[FreqBins, MelFrames];
        var spectrum = new Complex[WinLength];

        for (int t = 0; t < MelFrames; t++)
        {
            int offset = t * HopLength;
            Array.Clear(spectrum);
            for (int i = 0; i < WinLength; i++)
            {
                spectrum[i] = new Complex(samples[offset + i] * HannWin[i], 0.0);
            }

            Fourier.Forward(spectrum, FourierOptions.Matlab);

            for (int b = 0; b < FreqBins; b++)
            {
                double mag = spectrum[b].Magnitude;
                result[b, t] = (float)(mag * mag);
            }
        }

        return result;
    }

    private static float[] ApplyMelFilterbank(float[,] power)
    {
        float[] output = new float[MelBins * MelFrames];

        for (int m = 0; m < MelBins; m++)
        {
            for (int t = 0; t < MelFrames; t++)
            {
                double sum = 0;
                for (int b = 0; b < FreqBins; b++)
                {
                    sum += MelFilters[m, b] * power[b, t];
                }

                output[m * MelFrames + t] = MathF.Log10((float)Math.Max(sum, 1e-10));
            }
        }

        return output;
    }

    private static void WhisperNormalize(float[] mel)
    {
        float maxVal = float.NegativeInfinity;
        foreach (float v in mel)
        {
            if (v > maxVal) maxVal = v;
        }

        float clampMin = maxVal - 8f;
        for (int i = 0; i < mel.Length; i++)
        {
            mel[i] = (Math.Max(mel[i], clampMin) + 4f) / 4f;
        }
    }

    private static float[] BuildHannWindow(int n)
    {
        var w = new float[n];
        for (int i = 0; i < n; i++)
        {
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / (n - 1)));
        }

        return w;
    }

    private static float[,] BuildWhisperMelFilterbank()
    {
        const double fmax = 8000.0;
        const int sampleRate = 16000;

        double melMin = HzToMelHtk(0.0);
        double melMax = HzToMelHtk(fmax);

        // n_mels+2 evenly spaced mel points → convert to Hz
        double[] hzPoints = new double[MelBins + 2];
        for (int i = 0; i < hzPoints.Length; i++)
        {
            hzPoints[i] = MelToHzHtk(melMin + (melMax - melMin) * i / (MelBins + 1));
        }

        // FFT bin centre frequencies
        double[] fftFreqs = new double[FreqBins];
        for (int b = 0; b < FreqBins; b++)
        {
            fftFreqs[b] = b * (sampleRate / (double)WinLength);
        }

        var filters = new float[MelBins, FreqBins];
        for (int m = 0; m < MelBins; m++)
        {
            double lo = hzPoints[m];
            double center = hzPoints[m + 1];
            double hi = hzPoints[m + 2];
            double bwLeft = center - lo;
            double bwRight = hi - center;

            for (int b = 0; b < FreqBins; b++)
            {
                double f = fftFreqs[b];
                double w;
                if (f <= lo || f >= hi)
                    w = 0;
                else if (f <= center)
                    w = bwLeft > 0 ? (f - lo) / bwLeft : 0;
                else
                    w = bwRight > 0 ? (hi - f) / bwRight : 0;
                filters[m, b] = (float)w;
            }
        }

        return filters;
    }

    private static double HzToMelHtk(double hz) => 2595.0 * Math.Log10(1.0 + hz / 700.0);

    private static double MelToHzHtk(double mel) => 700.0 * (Math.Pow(10.0, mel / 2595.0) - 1.0);
}
