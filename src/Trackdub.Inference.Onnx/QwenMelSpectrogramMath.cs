using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Inference.Onnx;

internal static class QwenMelSpectrogramMath
{
    public static float[] BuildPeriodicHannWindow(int length)
    {
        var window = new float[length];
        for (int index = 0; index < length; index++)
        {
            window[index] = (float)(0.5 - (0.5 * Math.Cos((2 * Math.PI * index) / length)));
        }

        return window;
    }

    public static float[,] BuildMelFilterBank(int sampleRate, int fftSize, int melBins)
    {
        int frequencyBins = 1 + (fftSize / 2);
        var filters = new float[melBins, frequencyBins];
        double[] fftFrequencies = Enumerable.Range(0, frequencyBins)
            .Select(index => index * (sampleRate / 2d) / (frequencyBins - 1))
            .ToArray();

        double melMin = HertzToMel(0);
        double melMax = HertzToMel(sampleRate / 2d);
        double[] melPoints = Enumerable.Range(0, melBins + 2)
            .Select(index => melMin + ((melMax - melMin) * index / (melBins + 1d)))
            .ToArray();
        double[] hzPoints = melPoints.Select(MelToHertz).ToArray();

        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            double lower = hzPoints[melIndex];
            double center = hzPoints[melIndex + 1];
            double upper = hzPoints[melIndex + 2];
            double enorm = 2.0 / Math.Max(upper - lower, double.Epsilon);

            for (int binIndex = 0; binIndex < frequencyBins; binIndex++)
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

    public static float[,] ComputePowerSpectrum(
        ReadOnlySpan<float> paddedSamples,
        int frameCount,
        int fftSize,
        int hopLength,
        float[] hannWindow)
    {
        int frequencyBins = 1 + (fftSize / 2);
        var result = new float[frequencyBins, frameCount];
        var spectrum = new Complex[fftSize];

        for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
        {
            int sampleOffset = frameIndex * hopLength;
            Array.Clear(spectrum);
            for (int sampleIndex = 0; sampleIndex < fftSize; sampleIndex++)
            {
                double windowed = paddedSamples[sampleOffset + sampleIndex] * hannWindow[sampleIndex];
                spectrum[sampleIndex] = new Complex(windowed, 0);
            }

            Fourier.Forward(spectrum, FourierOptions.Matlab);
            for (int binIndex = 0; binIndex < frequencyBins; binIndex++)
            {
                double magnitude = spectrum[binIndex].Magnitude;
                result[binIndex, frameIndex] = (float)(magnitude * magnitude);
            }
        }

        return result;
    }

    public static float[,] ApplyMelFilters(float[,] powerSpectrum, float[,] melFilters)
    {
        int melBins = melFilters.GetLength(0);
        int frequencyBins = melFilters.GetLength(1);
        int frameCount = powerSpectrum.GetLength(1);
        var melSpectrum = new float[melBins, frameCount];
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                double sum = 0;
                for (int binIndex = 0; binIndex < frequencyBins; binIndex++)
                {
                    sum += melFilters[melIndex, binIndex] * powerSpectrum[binIndex, frameIndex];
                }

                melSpectrum[melIndex, frameIndex] = (float)sum;
            }
        }

        return melSpectrum;
    }

    public static void NormalizeLogMel(float[,] melSpectrum)
    {
        int melBins = melSpectrum.GetLength(0);
        int frameCount = melSpectrum.GetLength(1);
        float maxValue = float.NegativeInfinity;
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
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
        for (int melIndex = 0; melIndex < melBins; melIndex++)
        {
            for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
            {
                float normalized = Math.Max(melSpectrum[melIndex, frameIndex], minimumAllowed);
                melSpectrum[melIndex, frameIndex] = (normalized + 4f) / 4f;
            }
        }
    }

    public static ReadOnlySpan<float> ReflectPad(
        ReadOnlySpan<float> samples,
        int padding,
        out float[]? rentedArray)
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
