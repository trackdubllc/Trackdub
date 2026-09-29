using System.Buffers;
using System.Numerics;
using Trackdub.Inference.Onnx.Audio;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Inference.Onnx.SortFormer;

internal sealed class SortFormerFeatureExtractor
{
    public const int SampleRate = 16000;
    public const int FftSize = 512;
    public const int WindowLength = 400;
    public const int HopLength = 160;
    public const int MelBins = 128;

    private const float PreEmphasis = 0.97f;
    private const float LogZeroGuard = 5.9604645e-8f;
    private const int FrequencyBins = 1 + (FftSize / 2);

    private readonly float[] fftWindow = BuildCenteredPeriodicHannWindow();
    private readonly float[,] melFilters = BuildMelFilterBank();

    public SortFormerFeatureInputSet Extract(ReadOnlySpan<float> samples)
    {
        if (samples.Length == 0)
        {
            return new SortFormerFeatureInputSet(Array.Empty<float>(), 0, MelBins);
        }

        const int padAmount = FftSize / 2;
        int paddedLength = samples.Length + (padAmount * 2);
        float[] paddedSamples = ArrayPool<float>.Shared.Rent(paddedLength);

        try
        {
            // Zero-pad and preemphasize in one pass
            Array.Clear(paddedSamples, 0, padAmount);

            paddedSamples[padAmount] = samples[0];
            for (int index = 1; index < samples.Length; index++)
            {
                paddedSamples[padAmount + index] = samples[index] - (PreEmphasis * samples[index - 1]);
            }

            Array.Clear(paddedSamples, padAmount + samples.Length, paddedLength - (padAmount + samples.Length));

            int frameCount = 1 + ((paddedLength - FftSize) / HopLength);
            float[] powerSpectrum = ArrayPool<float>.Shared.Rent(FrequencyBins * frameCount);

            try
            {
                var spectrum = new Complex[FftSize];

                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    int sampleOffset = frameIndex * HopLength;
                    Array.Clear(spectrum);
                    for (int sampleIndex = 0; sampleIndex < FftSize; sampleIndex++)
                    {
                        double windowed = paddedSamples[sampleOffset + sampleIndex] * fftWindow[sampleIndex];
                        spectrum[sampleIndex] = new Complex(windowed, 0);
                    }

                    Fourier.Forward(spectrum, FourierOptions.Matlab);

                    int frameOffset = frameIndex * FrequencyBins;
                    for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                    {
                        double magnitude = spectrum[binIndex].Magnitude;
                        powerSpectrum[frameOffset + binIndex] = (float)(magnitude * magnitude);
                    }
                }

                int featureElementCount = frameCount * MelBins;
                float[] data = ArrayPool<float>.Shared.Rent(featureElementCount);
                bool transferredOwnership = false;
                try
                {
                    for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                    {
                        int powerOffset = frameIndex * FrequencyBins;
                        int dataOffset = frameIndex * MelBins;

                        for (int melIndex = 0; melIndex < MelBins; melIndex++)
                        {
                            double sum = 0;
                            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                            {
                                sum += melFilters[melIndex, binIndex] * powerSpectrum[powerOffset + binIndex];
                            }

                            data[dataOffset + melIndex] = MathF.Log((float)sum + LogZeroGuard);
                        }
                    }

                    SortFormerFeatureInputSet inputSet = new(data, frameCount, MelBins);
                    transferredOwnership = true;
                    return inputSet;
                }
                finally
                {
                    if (!transferredOwnership)
                    {
                        ArrayPool<float>.Shared.Return(data);
                    }
                }
            }
            finally
            {
                ArrayPool<float>.Shared.Return(powerSpectrum);
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(paddedSamples);
        }
    }

    /// <summary>
    /// Feature frame count for <paramref name="sampleFrameCount"/> mono samples:
    /// symmetric padding totals <see cref="FftSize"/>, so frames = 1 + samples / hop,
    /// including the padded final frame for exact multiples.
    /// </summary>
    internal static int GetFrameCount(long sampleFrameCount)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sampleFrameCount);

        if (sampleFrameCount == 0)
        {
            return 0; // parity with Extract(ReadOnlySpan<float>): empty input -> no frames
        }

        long frameCount = 1 + (sampleFrameCount / HopLength);
        return frameCount > int.MaxValue
            ? throw new InvalidOperationException($"Audio is too long for SortFormer diarization ({sampleFrameCount} samples).")
            : (int)frameCount;
    }

    /// <summary>
    /// Extracts log-mel features for frames [startFrame, startFrame + frameCount) directly from
    /// the source stream, reading only the sample window those frames need (plus one predecessor
    /// for pre-emphasis). Numerically equivalent to slicing <see cref="Extract(ReadOnlySpan{float})"/>.
    /// </summary>
    internal SortFormerFeatureInputSet Extract(
        IAudioSamples samples,
        int startFrame,
        int frameCount,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(samples);
        if (samples.SampleFrameCount < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(samples), "Sample count must be non-negative.");
        }

        int totalFrameCount = GetFrameCount(samples.SampleFrameCount);
        ArgumentOutOfRangeException.ThrowIfNegative(startFrame);

        // frameCount <= total first so total - frameCount cannot underflow, then the
        // subtraction form avoids int overflow on startFrame + frameCount.
        if (frameCount < 0 || frameCount > totalFrameCount || startFrame > totalFrameCount - frameCount)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frameCount),
                $"Frame range starting at {startFrame} with length {frameCount} exceeds {totalFrameCount} frames.");
        }

        if (frameCount == 0 || samples.SampleFrameCount == 0)
        {
            return new SortFormerFeatureInputSet(Array.Empty<float>(), 0, MelBins);
        }

        long sampleCount = samples.SampleFrameCount;
        const int padAmount = FftSize / 2;
        // Frame g reads source samples g*Hop + i - pad for i in [0, FftSize); the range's
        // needed window is [lo, hi] plus one predecessor sample for pre-emphasis.
        long lo = ((long)startFrame * HopLength) - padAmount;
        long hi = ((long)(startFrame + frameCount - 1) * HopLength) + (FftSize - 1) - padAmount;
        long readLo = Math.Max(0, lo - 1);
        long readHi = Math.Min(sampleCount - 1, hi);
        int windowLength = readHi >= readLo ? checked((int)(readHi - readLo + 1)) : 0;

        float[] sourceWindow = ArrayPool<float>.Shared.Rent(Math.Max(1, windowLength));
        try
        {
            if (windowLength > 0)
            {
                samples.ReadMonoSamples(readLo, sourceWindow.AsSpan(0, windowLength));
            }

            float Raw(long index) => index < readLo || index > readHi ? 0f : sourceWindow[index - readLo];

            float PreEmphasized(long sourceIndex) => sourceIndex switch
            {
                < 0 => 0f,
                _ when sourceIndex >= sampleCount => 0f,
                0 => Raw(0),
                _ => Raw(sourceIndex) - (PreEmphasis * Raw(sourceIndex - 1)),
            };

            int featureElementCount = frameCount * MelBins;
            float[] data = ArrayPool<float>.Shared.Rent(featureElementCount);
            bool transferredOwnership = false;
            try
            {
                var spectrum = new Complex[FftSize];
                var framePower = new float[FrequencyBins];
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    long frameSampleOrigin = ((long)(startFrame + frameIndex) * HopLength) - padAmount;
                    Array.Clear(spectrum);
                    for (int sampleIndex = 0; sampleIndex < FftSize; sampleIndex++)
                    {
                        double windowed = PreEmphasized(frameSampleOrigin + sampleIndex) * fftWindow[sampleIndex];
                        spectrum[sampleIndex] = new Complex(windowed, 0);
                    }

                    Fourier.Forward(spectrum, FourierOptions.Matlab);
                    for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                    {
                        double magnitude = spectrum[binIndex].Magnitude;
                        framePower[binIndex] = (float)(magnitude * magnitude);
                    }

                    int dataOffset = frameIndex * MelBins;
                    for (int melIndex = 0; melIndex < MelBins; melIndex++)
                    {
                        double sum = 0;
                        for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                        {
                            sum += melFilters[melIndex, binIndex] * framePower[binIndex];
                        }

                        data[dataOffset + melIndex] = MathF.Log((float)sum + LogZeroGuard);
                    }
                }

                SortFormerFeatureInputSet inputSet = new(data, frameCount, MelBins);
                transferredOwnership = true;
                return inputSet;
            }
            finally
            {
                if (!transferredOwnership)
                {
                    ArrayPool<float>.Shared.Return(data);
                }
            }
        }
        finally
        {
            ArrayPool<float>.Shared.Return(sourceWindow);
        }
    }

    private static float[] BuildCenteredPeriodicHannWindow()
    {
        var window = new float[FftSize];
        int offset = (FftSize - WindowLength) / 2;
        for (int index = 0; index < WindowLength; index++)
        {
            window[offset + index] = (float)(0.5d - (0.5d * Math.Cos((2d * Math.PI * index) / WindowLength)));
        }

        return window;
    }

    private static float[,] BuildMelFilterBank()
    {
        var filters = new float[MelBins, FrequencyBins];
        double[] fftFrequencies = Enumerable.Range(0, FrequencyBins)
            .Select(index => (index * (double)SampleRate) / FftSize)
            .ToArray();

        double melMin = HertzToMel(0);
        double melMax = HertzToMel(SampleRate / 2d);
        double[] melPoints = Enumerable.Range(0, MelBins + 2)
            .Select(index => MelToHertz(melMin + ((melMax - melMin) * index / (MelBins + 1d))))
            .ToArray();

        double[] differences = melPoints
            .Zip(melPoints.Skip(1), static (left, right) => right - left)
            .ToArray();

        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double frequency = fftFrequencies[binIndex];
                double lower = (frequency - melPoints[melIndex]) / Math.Max(differences[melIndex], double.Epsilon);
                double upper = (melPoints[melIndex + 2] - frequency) / Math.Max(differences[melIndex + 1], double.Epsilon);
                double weight = Math.Max(0, Math.Min(lower, upper));
                double enorm = 2d / Math.Max(melPoints[melIndex + 2] - melPoints[melIndex], double.Epsilon);
                filters[melIndex, binIndex] = (float)(weight * enorm);
            }
        }

        return filters;
    }

    private static double HertzToMel(double frequencyHertz)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4d) / 27d;

        return frequencyHertz >= minLogHertz
            ? minLogMel + (Math.Log(frequencyHertz / minLogHertz) / logStep)
            : frequencyHertz / fSp;
    }

    private static double MelToHertz(double mel)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4d) / 27d;

        return mel >= minLogMel
            ? minLogHertz * Math.Exp(logStep * (mel - minLogMel))
            : mel * fSp;
    }
}

internal sealed class SortFormerFeatureInputSet : IDisposable
{
    private float[]? data;
    private readonly int elementCount;

    public SortFormerFeatureInputSet(float[] data, int frameCount, int featureCount)
    {
        ArgumentNullException.ThrowIfNull(data);

        this.data = data;
        FrameCount = frameCount;
        FeatureCount = featureCount;
        elementCount = checked(frameCount * featureCount);
    }

    public ReadOnlySpan<float> Data => (data ?? throw new ObjectDisposedException(nameof(SortFormerFeatureInputSet)))
        .AsSpan(0, elementCount);

    public int FrameCount { get; }
    public int FeatureCount { get; }

    public void CopyFramesTo(
        float[] destination,
        int destinationFrameOffset,
        int sourceFrameOffset,
        int frameCount)
    {
        ArgumentNullException.ThrowIfNull(destination);

        if (frameCount <= 0)
        {
            return;
        }

        float[] source = data ?? throw new ObjectDisposedException(nameof(SortFormerFeatureInputSet));
        int sourceOffset = sourceFrameOffset * FeatureCount;
        int destinationOffset = destinationFrameOffset * FeatureCount;
        Array.Copy(source, sourceOffset, destination, destinationOffset, frameCount * FeatureCount);
    }

    public void Dispose()
    {
        float[]? rentedData = Interlocked.Exchange(ref data, null);
        if (rentedData is { Length: > 0 })
        {
            ArrayPool<float>.Shared.Return(rentedData);
        }
    }
}
