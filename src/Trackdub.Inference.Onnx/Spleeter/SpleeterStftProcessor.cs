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
