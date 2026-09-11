using System.Numerics;
using MathNet.Numerics.IntegralTransforms;

namespace Trackdub.Media.Mixing;

internal static class SpectralEnvelopeAnalyzer
{
    private const float MinVoicedRms = 0.001f;
    private const float Epsilon = 1e-8f;

    public static float[]? Extract(ReadOnlySpan<float> samples, StftProcessor stft, int lifterOrder = 32)
    {
        if (samples.Length < stft.FftSize)
            return null;

        Complex[][] frames = stft.Forward(samples);
        int binCount = stft.BinCount;
        var envelopeAccum = new double[binCount];
        int voicedFrames = 0;

        foreach (Complex[] frame in frames)
        {
            double rmsSum = 0d;
            for (int k = 0; k < binCount; k++)
                rmsSum += frame[k].Magnitude * frame[k].Magnitude;
            if (Math.Sqrt(rmsSum / binCount) < MinVoicedRms)
                continue;

            float[] logMag = new float[binCount];
            for (int k = 0; k < binCount; k++)
                logMag[k] = (float)Math.Log(frame[k].Magnitude + Epsilon);

            float[]? envelope = CepstralSmooth(logMag, lifterOrder);
            if (envelope is null)
                continue;

            for (int k = 0; k < binCount; k++)
                envelopeAccum[k] += envelope[k];
            voicedFrames++;
        }

        if (voicedFrames == 0)
            return null;

        var result = new float[binCount];
        for (int k = 0; k < binCount; k++)
            result[k] = (float)(envelopeAccum[k] / voicedFrames);
        return result;
    }

    private static float[]? CepstralSmooth(float[] logMag, int lifterOrder)
    {
        int binCount = logMag.Length;
        // Reconstruct full-size symmetric log-spectrum for IFFT (size = original fftSize = (binCount-1)*2)
        int cepSize = (binCount - 1) * 2;
        if (cepSize <= 0)
            return null;

        var buffer = new Complex[cepSize];
        buffer[0] = new Complex(logMag[0], 0d);
        for (int k = 1; k < binCount - 1; k++)
        {
            buffer[k] = new Complex(logMag[k], 0d);
            buffer[cepSize - k] = new Complex(logMag[k], 0d);
        }
        buffer[binCount - 1] = new Complex(logMag[binCount - 1], 0d);

        Fourier.Inverse(buffer, FourierOptions.Matlab);

        // Lifter: zero out high-quefrency coefficients
        for (int n = lifterOrder + 1; n < cepSize - lifterOrder; n++)
            buffer[n] = Complex.Zero;

        Fourier.Forward(buffer, FourierOptions.Matlab);

        var envelope = new float[binCount];
        for (int k = 0; k < binCount; k++)
            envelope[k] = (float)Math.Exp(buffer[k].Real);
        return envelope;
    }
}
