using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Media.Mixing;

internal sealed class StftProcessor
{
    private readonly int fftSize;
    private readonly int hopSize;
    private readonly float[] window;

    public StftProcessor(int fftSize = 1024, int hopSize = 256)
    {
        this.fftSize = fftSize;
        this.hopSize = hopSize;
        window = BuildHannWindow(fftSize);
    }

    public int FftSize => fftSize;
    public int HopSize => hopSize;
    public int BinCount => fftSize / 2 + 1;

    public Complex[][] Forward(ReadOnlySpan<float> samples)
    {
        int numFrames = samples.Length < fftSize
            ? 1
            : (int)Math.Ceiling((double)(samples.Length - fftSize) / hopSize) + 1;

        var frames = new Complex[numFrames][];
        var buffer = new Complex[fftSize];

        for (int frameIndex = 0; frameIndex < numFrames; frameIndex++)
        {
            int start = frameIndex * hopSize;
            for (int i = 0; i < fftSize; i++)
            {
                int sampleIndex = start + i;
                double sample = sampleIndex < samples.Length ? samples[sampleIndex] : 0.0;
                buffer[i] = new Complex(sample * window[i], 0.0);
            }

            Fourier.Forward(buffer, FourierOptions.Matlab);

            var bins = new Complex[BinCount];
            for (int i = 0; i < BinCount; i++)
                bins[i] = buffer[i];
            frames[frameIndex] = bins;
        }

        return frames;
    }

    public float[] Inverse(Complex[][] frames, int outputLength)
    {
        var output = new double[outputLength];
        var windowSquaredSum = new double[outputLength];
        var buffer = new Complex[fftSize];

        for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            Complex[] bins = frames[frameIndex];
            buffer[0] = bins[0];
            for (int i = 1; i < BinCount; i++)
                buffer[i] = bins[i];
            for (int i = BinCount; i < fftSize; i++)
                buffer[i] = Complex.Conjugate(bins[fftSize - i]);

            Fourier.Inverse(buffer, FourierOptions.Matlab);

            int start = frameIndex * hopSize;
            for (int i = 0; i < fftSize; i++)
            {
                int pos = start + i;
                if (pos >= outputLength) break;
                output[pos] += buffer[i].Real * window[i];
                windowSquaredSum[pos] += window[i] * (double)window[i];
            }
        }

        var result = new float[outputLength];
        for (int i = 0; i < outputLength; i++)
            result[i] = windowSquaredSum[i] > 1e-8 ? (float)(output[i] / windowSquaredSum[i]) : 0f;
        return result;
    }

    private static float[] BuildHannWindow(int size)
    {
        var w = new float[size];
        for (int i = 0; i < size; i++)
            w[i] = (float)(0.5 - 0.5 * Math.Cos(2.0 * Math.PI * i / size));
        return w;
    }
}
