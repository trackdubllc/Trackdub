using System.Buffers;
using MathNet.Numerics.IntegralTransforms;
using System.Numerics;

namespace Trackdub.Inference.Onnx.Spleeter;

internal sealed class SpleeterStftProcessor
{
    private const int N_Fft = SpleeterModelConstants.Nfft;
    private const int Hop = SpleeterModelConstants.Hop;
    private const int MaxFreqs = SpleeterModelConstants.MaxFreqBins;
    public const int PadTo = SpleeterModelConstants.TimePad;

    private readonly float[] window;

    public SpleeterStftProcessor()
    {
        window = new float[N_Fft];
        for (int i = 0; i < N_Fft; i++)
        {
            window[i] = (float)(0.5 * (1.0 - Math.Cos(2.0 * Math.PI * i / N_Fft)));
        }
    }

    /// <summary>Total padded frame count for a mono input of <paramref name="sampleCount"/>.</summary>
    internal int GetTargetFrameCount(int sampleCount)
    {
        int baseFrames = sampleCount >= N_Fft ? 1 + ((sampleCount - N_Fft) / Hop) : 1;
        return SpleeterModelConstants.PadTimeFrames(baseFrames);
    }

    /// <summary>
    /// Forward STFT of <paramref name="frameCount"/> consecutive frames starting at
    /// <paramref name="startFrame"/>, computed identically to the equivalent slice of the
    /// global <see cref="Forward"/> on each channel. Magnitudes/phases are laid out as
    /// [channel, frame, freq] = model input [2, 1, frameCount, <see cref="SpleeterModelConstants.MaxFreqBins"/>] (or [1, 2, ...] for the splits-first export).
    /// </summary>
    internal SpleeterStftBlock ForwardBlock(
        ReadOnlySpan<float> left,
        ReadOnlySpan<float> right,
        int startFrame,
        int frameCount)
    {
        if (startFrame < 0 || frameCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(frameCount));
        }

        float[] magnitude = ArrayPool<float>.Shared.Rent(2 * frameCount * MaxFreqs);
        float[] phase = ArrayPool<float>.Shared.Rent(2 * frameCount * MaxFreqs);
        var block = new SpleeterStftBlock(magnitude, phase, startFrame, frameCount);
        try
        {
            Complex[] buffer = new Complex[N_Fft];
            for (int channel = 0; channel < 2; channel++)
            {
                ReadOnlySpan<float> input = channel == 0 ? left : right;
                for (int frame = 0; frame < frameCount; frame++)
                {
                    int startSample = (startFrame + frame) * Hop;
                    for (int i = 0; i < N_Fft; i++)
                    {
                        int sampleIdx = startSample + i;
                        float val = sampleIdx < input.Length ? input[sampleIdx] : 0f;
                        buffer[i] = new Complex(val * window[i], 0);
                    }

                    Fourier.Forward(buffer, FourierOptions.NoScaling);

                    int offset = ((channel * frameCount) + frame) * MaxFreqs;
                    for (int k = 0; k < MaxFreqs; k++)
                    {
                        magnitude[offset + k] = (float)buffer[k].Magnitude;
                        phase[offset + k] = (float)buffer[k].Phase;
                    }
                }
            }

            return block;
        }
        catch
        {
            block.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Applies per-element soft masks to a block's magnitudes/phases, inverse-transforms each
    /// frame/channel, and overlap-adds the channel average directly into the full-length mono
    /// outputs. <paramref name="windowSum"/> accumulates the squared window once per output
    /// sample per frame (shared by both channels and stems, so the average of the two
    /// normalized channels equals the normalized average).
    /// </summary>
    internal void OverlapAddMaskedBlock(
        SpleeterStftBlock block,
        ReadOnlySpan<float> vocalsMask,
        ReadOnlySpan<float> accompanimentMask,
        float[] vocalsMono,
        float[] accompanimentMono,
        float[] windowSum,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(block);
        int expected = 2 * block.FrameCount * MaxFreqs;
        if (vocalsMask.Length != expected || accompanimentMask.Length != expected)
        {
            throw new ArgumentException(
                $"Masks must contain {expected} elements (2 * {block.FrameCount} * {MaxFreqs}).");
        }
        if (vocalsMono.Length != accompanimentMono.Length || vocalsMono.Length != windowSum.Length)
        {
            throw new ArgumentException("Output and window-sum arrays must have identical lengths.");
        }

        Complex[] vocalsBuffer = new Complex[N_Fft];
        Complex[] accBuffer = new Complex[N_Fft];
        ReadOnlySpan<float> magnitudes = block.Magnitudes.Span;
        ReadOnlySpan<float> phases = block.Phases.Span;

        for (int frame = 0; frame < block.FrameCount; frame++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int startSample = (block.StartFrame + frame) * Hop;

            for (int channel = 0; channel < 2; channel++)
            {
                int offset = ((channel * block.FrameCount) + frame) * MaxFreqs;
                for (int k = 0; k < MaxFreqs; k++)
                {
                    float mag = magnitudes[offset + k];
                    float ph = phases[offset + k];
                    SpleeterModelConstants.ComputeSoftMasks(
                        vocalsMask[offset + k], accompanimentMask[offset + k],
                        out float maskVocals, out float maskAcc);
                    vocalsBuffer[k] = Complex.FromPolarCoordinates(mag * maskVocals, ph);
                    accBuffer[k] = Complex.FromPolarCoordinates(mag * maskAcc, ph);
                }

                MirrorAndInvert(vocalsBuffer);
                MirrorAndInvert(accBuffer);

                for (int i = 0; i < N_Fft; i++)
                {
                    long sampleIndex = (long)startSample + i;
                    if (sampleIndex >= vocalsMono.Length)
                    {
                        break;
                    }

                    float w = window[i];
                    vocalsMono[sampleIndex] += (float)(vocalsBuffer[i].Real / N_Fft) * w * 0.5f;
                    accompanimentMono[sampleIndex] += (float)(accBuffer[i].Real / N_Fft) * w * 0.5f;
                    if (channel == 0)
                    {
                        windowSum[sampleIndex] += w * w;
                    }
                }
            }
        }
    }

    /// <summary>Divides overlap-added outputs by the shared window-sum, matching legacy Inverse.</summary>
    internal static void NormalizeOverlapAdd(float[] vocalsMono, float[] accompanimentMono, float[] windowSum)
    {
        for (int i = 0; i < windowSum.Length; i++)
        {
            float w = windowSum[i];
            if (w > 1e-7f)
            {
                vocalsMono[i] /= w;
                accompanimentMono[i] /= w;
            }
        }
    }

    private static void MirrorAndInvert(Complex[] buffer)
    {
        for (int k = MaxFreqs; k < N_Fft; k++)
        {
            buffer[k] = Complex.Zero;
        }

        for (int k = 1; k < MaxFreqs; k++)
        {
            buffer[N_Fft - k] = Complex.Conjugate(buffer[k]);
        }

        Fourier.Inverse(buffer, FourierOptions.NoScaling);
    }

    public (float[] Magnitude, float[] Phase, int TargetFrames) Forward(float[] input)
    {
        int baseFrames = input.Length >= N_Fft ? 1 + ((input.Length - N_Fft) / Hop) : 1;
        // sherpa-onnx separate_onnx.py: padding = 512 - (num_frames % 512) when > 0.
        // Exact multiples still receive another full pad block.
        int targetFrames = SpleeterModelConstants.PadTimeFrames(baseFrames);

        float[] magnitude = new float[targetFrames * MaxFreqs];
        float[] phase = new float[targetFrames * MaxFreqs];

        Complex[] buffer = new Complex[N_Fft];

        for (int frame = 0; frame < targetFrames; frame++)
        {
            int startSample = frame * Hop;
            for (int i = 0; i < N_Fft; i++)
            {
                int sampleIdx = startSample + i;
                float val = sampleIdx < input.Length ? input[sampleIdx] : 0f;
                buffer[i] = new Complex(val * window[i], 0);
            }

            Fourier.Forward(buffer, FourierOptions.NoScaling);

            int offset = frame * MaxFreqs;
            for (int k = 0; k < MaxFreqs; k++)
            {
                magnitude[offset + k] = (float)buffer[k].Magnitude;
                phase[offset + k] = (float)buffer[k].Phase;
            }
        }

        return (magnitude, phase, targetFrames);
    }

    public float[] Inverse(float[] magnitude, float[] phase, int targetFrames, int originalLength)
    {
        float[] output = new float[((targetFrames - 1) * Hop) + N_Fft];
        float[] windowSum = new float[output.Length];

        Complex[] buffer = new Complex[N_Fft];

        for (int frame = 0; frame < targetFrames; frame++)
        {
            int offset = frame * MaxFreqs;
            for (int k = 0; k < MaxFreqs; k++)
            {
                float mag = magnitude[offset + k];
                float ph = phase[offset + k];
                buffer[k] = Complex.FromPolarCoordinates(mag, ph);
            }

            for (int k = MaxFreqs; k < N_Fft; k++)
            {
                buffer[k] = Complex.Zero;
            }

            for (int k = 1; k < MaxFreqs; k++)
            {
                buffer[N_Fft - k] = Complex.Conjugate(buffer[k]);
            }

            Fourier.Inverse(buffer, FourierOptions.NoScaling);

            int startSample = frame * Hop;
            for (int i = 0; i < N_Fft; i++)
            {
                float val = (float)(buffer[i].Real / N_Fft);
                output[startSample + i] += val * window[i];
                windowSum[startSample + i] += window[i] * window[i];
            }
        }

        float[] trimmed = new float[originalLength];
        for (int i = 0; i < originalLength; i++)
        {
            float w = windowSum[i];
            trimmed[i] = w > 1e-7f ? output[i] / w : output[i];
        }

        return trimmed;
    }
}

/// <summary>
/// Pooled magnitude/phase arrays for one <see cref="SpleeterModelConstants.TimePad"/>-frame
/// STFT block, laid out as [channel, frame, freq] = model input [2, 1, TimePad, MaxFreqBins] (or [1, 2, ...] for the splits-first export).
/// </summary>
internal sealed class SpleeterStftBlock : IDisposable
{
    private float[]? magnitudes;
    private float[]? phases;
    private readonly int elementCount;

    public SpleeterStftBlock(float[] magnitudes, float[] phases, int startFrame, int frameCount)
    {
        this.magnitudes = magnitudes;
        this.phases = phases;
        StartFrame = startFrame;
        FrameCount = frameCount;
        elementCount = checked(2 * frameCount * SpleeterModelConstants.MaxFreqBins);
    }

    public int StartFrame { get; }

    public int FrameCount { get; }

    public Memory<float> Magnitudes =>
        (magnitudes ?? throw new ObjectDisposedException(nameof(SpleeterStftBlock)))
            .AsMemory(0, elementCount);

    public Memory<float> Phases =>
        (phases ?? throw new ObjectDisposedException(nameof(SpleeterStftBlock)))
            .AsMemory(0, elementCount);

    public void Dispose()
    {
        float[]? rented = Interlocked.Exchange(ref magnitudes, null);
        if (rented is not null)
        {
            ArrayPool<float>.Shared.Return(rented);
        }

        rented = Interlocked.Exchange(ref phases, null);
        if (rented is not null)
        {
            ArrayPool<float>.Shared.Return(rented);
        }
    }
}
