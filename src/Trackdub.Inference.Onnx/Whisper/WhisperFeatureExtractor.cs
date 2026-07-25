using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.Whisper;

internal sealed class WhisperFeatureExtractor
{
    private const int SampleRate = 16000;
    private const int FftSize = 400;
    private const int HopLength = 160;
    private const int MelBins = 80;
    private const int MaxSamples = 480000;
    private const int MaxFrames = 3000;
    private const int FrequencyBins = 1 + (FftSize / 2);
    private readonly float[] hannWindow = BuildPeriodicHannWindow(FftSize);
    private readonly float[,] melFilters = BuildMelFilterBank();

    public DenseTensor<float> Extract(ReadOnlySpan<float> inputSamples)
    {
        float[]? rentedPaddedOrTrimmed = null;
        float[]? rentedPaddedForStft = null;
        try
        {
            ReadOnlySpan<float> paddedOrTrimmed = PadOrTrim(inputSamples, MaxSamples, out rentedPaddedOrTrimmed);
            ReadOnlySpan<float> paddedForStft = ReflectPad(paddedOrTrimmed, FftSize / 2, out rentedPaddedForStft);
            float[,] powerSpectrum = ComputePowerSpectrum(paddedForStft);
            float[,] melSpectrum = ApplyMelFilters(powerSpectrum);
            NormalizeLogMel(melSpectrum);

            var data = new float[MelBins * MaxFrames];
            for (int melIndex = 0; melIndex < MelBins; melIndex++)
            {
                for (int frameIndex = 0; frameIndex < MaxFrames; frameIndex++)
                {
                    data[(melIndex * MaxFrames) + frameIndex] = melSpectrum[melIndex, frameIndex];
                }
            }

            return new DenseTensor<float>(data, [1, MelBins, MaxFrames]);
        }
        finally
        {
            if (rentedPaddedOrTrimmed != null)
                System.Buffers.ArrayPool<float>.Shared.Return(rentedPaddedOrTrimmed);
            if (rentedPaddedForStft != null)
                System.Buffers.ArrayPool<float>.Shared.Return(rentedPaddedForStft);
        }
    }

    public static float[] PrepareSamples(float[] inputSamples, int inputSampleRate) =>
        inputSampleRate == SampleRate
            ? inputSamples
            : Audio.AudioResampler.Resample(inputSamples, inputSampleRate, SampleRate);

    private float[,] ComputePowerSpectrum(ReadOnlySpan<float> paddedSamples)
    {
        int frameCount = 1 + ((paddedSamples.Length - FftSize) / HopLength);
        var result = new float[FrequencyBins, MaxFrames];
        var spectrum = new Complex[FftSize];

        for (int frameIndex = 0; frameIndex < frameCount - 1 && frameIndex < MaxFrames; frameIndex++)
        {
            int sampleOffset = frameIndex * HopLength;
            Array.Clear(spectrum);
            for (int sampleIndex = 0; sampleIndex < FftSize; sampleIndex++)
            {
                double windowed = paddedSamples[sampleOffset + sampleIndex] * hannWindow[sampleIndex];
                spectrum[sampleIndex] = new Complex(windowed, 0);
            }

            Fourier.Forward(spectrum, FourierOptions.Matlab);

            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double magnitude = spectrum[binIndex].Magnitude;
                result[binIndex, frameIndex] = (float)(magnitude * magnitude);
            }
        }

        return result;
    }

    private float[,] ApplyMelFilters(float[,] powerSpectrum)
    {
        var melSpectrum = new float[MelBins, MaxFrames];
        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < MaxFrames; frameIndex++)
            {
                double sum = 0;
                for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
                {
                    sum += melFilters[melIndex, binIndex] * powerSpectrum[binIndex, frameIndex];
                }

                melSpectrum[melIndex, frameIndex] = (float)sum;
            }
        }

        return melSpectrum;
    }

    private static void NormalizeLogMel(float[,] melSpectrum)
    {
        float maxValue = float.NegativeInfinity;
        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < MaxFrames; frameIndex++)
            {
                float clamped = Math.Max(melSpectrum[melIndex, frameIndex], 1e-10f);
                float logValue = MathF.Log10(clamped);
                melSpectrum[melIndex, frameIndex] = logValue;
                if (logValue > maxValue)
                {
                    maxValue = logValue;
                }
            }
        }

        float minimumAllowed = maxValue - 8f;
        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < MaxFrames; frameIndex++)
            {
                float normalized = Math.Max(melSpectrum[melIndex, frameIndex], minimumAllowed);
                melSpectrum[melIndex, frameIndex] = (normalized + 4f) / 4f;
            }
        }
    }

    private static ReadOnlySpan<float> PadOrTrim(ReadOnlySpan<float> inputSamples, int targetLength, out float[]? rentedArray)
    {
        if (inputSamples.Length == targetLength)
        {
            rentedArray = null;
            return inputSamples;
        }

        rentedArray = System.Buffers.ArrayPool<float>.Shared.Rent(targetLength);
        Array.Clear(rentedArray, 0, targetLength);
        int copyLength = Math.Min(inputSamples.Length, targetLength);
        inputSamples.Slice(0, copyLength).CopyTo(rentedArray);
        return new ReadOnlySpan<float>(rentedArray, 0, targetLength);
    }

    private static ReadOnlySpan<float> ReflectPad(ReadOnlySpan<float> samples, int padding, out float[]? rentedArray)
    {
        if (samples.Length == 0 || padding <= 0)
        {
            rentedArray = null;
            return samples;
        }

        int targetLength = samples.Length + (padding * 2);
        rentedArray = System.Buffers.ArrayPool<float>.Shared.Rent(targetLength);
        Array.Clear(rentedArray, 0, targetLength);
        for (int index = 0; index < padding; index++)
        {
            rentedArray[index] = samples[Math.Min(padding - index, samples.Length - 1)];
        }

        samples.CopyTo(new Span<float>(rentedArray, padding, samples.Length));

        for (int index = 0; index < padding; index++)
        {
            int sourceIndex = Math.Max(samples.Length - 2 - index, 0);
            rentedArray[padding + samples.Length + index] = samples[sourceIndex];
        }

        return new ReadOnlySpan<float>(rentedArray, 0, targetLength);
    }

    private static float[] BuildPeriodicHannWindow(int length)
    {
        var window = new float[length];
        for (int index = 0; index < length; index++)
        {
            window[index] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / length)));
        }

        return window;
    }

    private static float[,] BuildMelFilterBank()
    {
        var filters = new float[MelBins, FrequencyBins];
        double[] fftFrequencies = Enumerable.Range(0, FrequencyBins)
            .Select(index => index * (SampleRate / 2d) / (FrequencyBins - 1))
            .ToArray();

        double melMin = HertzToMel(0);
        double melMax = HertzToMel(SampleRate / 2d);
        double[] melPoints = Enumerable.Range(0, MelBins + 2)
            .Select(index => melMin + ((melMax - melMin) * index / (MelBins + 1d)))
            .ToArray();
        double[] hzPoints = melPoints.Select(MelToHertz).ToArray();

        for (int melIndex = 0; melIndex < MelBins; melIndex++)
        {
            double lower = hzPoints[melIndex];
            double center = hzPoints[melIndex + 1];
            double upper = hzPoints[melIndex + 2];
            double enorm = 2.0 / Math.Max(upper - lower, double.Epsilon);

            for (int binIndex = 0; binIndex < FrequencyBins; binIndex++)
            {
                double frequency = fftFrequencies[binIndex];
                double lowerSlope = (frequency - lower) / Math.Max(center - lower, double.Epsilon);
                double upperSlope = (upper - frequency) / Math.Max(upper - center, double.Epsilon);
                double weight = Math.Max(0, Math.Min(lowerSlope, upperSlope));
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
        double logStep = Math.Log(6.4) / 27d;

        return frequencyHertz >= minLogHertz
            ? minLogMel + (Math.Log(frequencyHertz / minLogHertz) / logStep)
            : frequencyHertz / fSp;
    }

    private static double MelToHertz(double mel)
    {
        const double fSp = 200d / 3d;
        const double minLogHertz = 1000d;
        double minLogMel = minLogHertz / fSp;
        double logStep = Math.Log(6.4) / 27d;

        return mel >= minLogMel
            ? minLogHertz * Math.Exp(logStep * (mel - minLogMel))
            : mel * fSp;
    }
}