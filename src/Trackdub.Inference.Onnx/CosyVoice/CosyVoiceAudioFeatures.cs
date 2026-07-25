using System.Numerics;
using MathNet.Numerics.IntegralTransforms;
using Trackdub.Inference.Onnx.LipSynthesis;
using Trackdub.Inference.Onnx.Qwen3Tts.Audio;

namespace Trackdub.Inference.Onnx.CosyVoice;

internal static class CosyVoiceAudioFeatures
{
    public static float[] LoadMonoResampled(string wavPath, int targetSampleRate)
    {
        (float[] samples, int sampleRate) = MelSpectrogram.ReadWav(wavPath);
        return sampleRate == targetSampleRate
            ? samples
            : MelSpectrogram.Resample(samples, sampleRate, targetSampleRate);
    }

    public static float[] ExtractCampplusFbank(float[] pcm16Khz)
    {
        const int frameLength = 400;
        const int frameShift = 160;
        const int numMelBins = 80;
        float[,] mel = ComputeMelSpectrogram(
            pcm16Khz,
            sampleRate: CosyVoiceConstants.CampplusSampleRate,
            nFft: frameLength,
            hopLength: frameShift,
            nMels: numMelBins,
            fMin: 20f,
            fMax: 8000f,
            useLog: true,
            center: false);

        int frames = mel.GetLength(0);
        var flattened = new float[frames * numMelBins];
        for (int frame = 0; frame < frames; frame++)
        {
            for (int bin = 0; bin < numMelBins; bin++)
            {
                flattened[(frame * numMelBins) + bin] = mel[frame, bin];
            }
        }

        // Per-utterance mean normalization (Kaldi fbank style used by CosyVoice frontend).
        double mean = 0d;
        for (int i = 0; i < flattened.Length; i++)
        {
            mean += flattened[i];
        }

        mean /= Math.Max(1, flattened.Length);
        for (int i = 0; i < flattened.Length; i++)
        {
            flattened[i] = (float)(flattened[i] - mean);
        }

        return flattened;
    }

    public static (float[] feats, int frames) ExtractSpeechTokenizerMel(float[] pcm16Khz)
    {
        float[] mel = ComputeWhisper128Mel(pcm16Khz);
        int frames = mel.Length / CosyVoiceConstants.SpeechTokenizerMelBins;
        return (mel, frames);
    }

    public static float[,] ExtractPromptMel(float[] pcm22050Hz)
    {
        return ComputeMelSpectrogram(
            pcm22050Hz,
            sampleRate: CosyVoiceConstants.SampleRate,
            nFft: 1024,
            hopLength: CosyVoiceConstants.MelHop,
            nMels: CosyVoiceConstants.MelBins,
            fMin: 0f,
            fMax: 8000f,
            useLog: false,
            center: false);
    }

    private static float[] ComputeWhisper128Mel(float[] pcm16Khz)
    {
        const int nFft = 400;
        const int hop = 160;
        const int nMels = 128;
        int paddedLength = pcm16Khz.Length + nFft;
        var padded = new float[paddedLength];
        Array.Copy(pcm16Khz, padded, pcm16Khz.Length);

        float[,] melFilters = BuildWhisperMelFilters(nMels, nFft / 2 + 1, CosyVoiceConstants.SpeechTokenizerSampleRate, nFft);
        float[] window = BuildHann(nFft);
        int frames = 1 + Math.Max(0, (paddedLength - nFft) / hop);
        var output = new float[nMels * frames];
        var real = new double[nFft];
        var imag = new double[nFft];

        for (int frame = 0; frame < frames; frame++)
        {
            int start = frame * hop;
            for (int i = 0; i < nFft; i++)
            {
                float sample = start + i < padded.Length ? padded[start + i] : 0f;
                real[i] = sample * window[i];
                imag[i] = 0d;
            }

            FftInPlace(real, imag);
            for (int mel = 0; mel < nMels; mel++)
            {
                double energy = 1e-10;
                for (int bin = 0; bin < nFft / 2 + 1; bin++)
                {
                    double magnitude = Math.Sqrt((real[bin] * real[bin]) + (imag[bin] * imag[bin])) + 1e-10;
                    energy += magnitude * melFilters[mel, bin];
                }

                output[(mel * frames) + frame] = (float)Math.Log10(energy);
            }
        }

        float max = output.Max();
        float min = max - 8f;
        for (int i = 0; i < output.Length; i++)
        {
            output[i] = Math.Max(min, Math.Min(max, (output[i] + 4f) / 4f));
        }

        return output;
    }

    private static float[,] ComputeMelSpectrogram(
        float[] samples,
        int sampleRate,
        int nFft,
        int hopLength,
        int nMels,
        float fMin,
        float fMax,
        bool useLog,
        bool center)
    {
        int padding = center ? (nFft - hopLength) / 2 : 0;
        var padded = new float[samples.Length + (padding * 2)];
        if (padding > 0)
        {
            ReflectPad(samples, padded, padding);
        }
        else
        {
            Array.Copy(samples, padded, samples.Length);
        }

        int frames = 1 + Math.Max(0, (padded.Length - nFft) / hopLength);
        float[,] mel = new float[frames, nMels];
        float fMaxResolved = fMax > 0 ? fMax : sampleRate / 2f;
        float[,] filters = BuildMelFilterBank(nMels, nFft / 2 + 1, sampleRate, fMin, fMaxResolved, nFft);
        float[] window = BuildHann(nFft);
        var real = new double[nFft];
        var imag = new double[nFft];

        for (int frame = 0; frame < frames; frame++)
        {
            int start = frame * hopLength;
            for (int i = 0; i < nFft; i++)
            {
                float sample = start + i < padded.Length ? padded[start + i] : 0f;
                real[i] = sample * window[i];
                imag[i] = 0d;
            }

            FftInPlace(real, imag);
            for (int m = 0; m < nMels; m++)
            {
                double energy = 1e-10;
                for (int k = 0; k < nFft / 2 + 1; k++)
                {
                    double magnitude = Math.Sqrt((real[k] * real[k]) + (imag[k] * imag[k])) + 1e-10;
                    energy += magnitude * filters[m, k];
                }

                mel[frame, m] = useLog ? (float)Math.Log(energy) : (float)energy;
            }
        }

        return mel;
    }

    private static void ReflectPad(float[] input, float[] output, int padding)
    {
        for (int i = 0; i < padding; i++)
        {
            output[padding - 1 - i] = input[Math.Min(i + 1, input.Length - 1)];
        }

        Array.Copy(input, 0, output, padding, input.Length);
        for (int i = 0; i < padding; i++)
        {
            output[padding + input.Length + i] = input[Math.Max(0, input.Length - 2 - i)];
        }
    }

    private static float[] BuildHann(int size)
    {
        var window = new float[size];
        for (int i = 0; i < size; i++)
        {
            window[i] = (float)(0.5d - (0.5d * Math.Cos((2d * Math.PI * i) / Math.Max(1, size - 1))));
        }

        return window;
    }

    private static float[,] BuildMelFilterBank(int nMels, int nFreqs, int sampleRate, float fMin, float fMax, int nFft)
    {
        static float HzToMel(float hz) => 2595f * (float)Math.Log10(1f + (hz / 700f));
        static float MelToHz(float mel) => 700f * ((float)Math.Pow(10f, mel / 2595f) - 1f);

        float melMin = HzToMel(fMin);
        float melMax = HzToMel(fMax);
        float[] melPoints = new float[nMels + 2];
        for (int i = 0; i < melPoints.Length; i++)
        {
            melPoints[i] = melMin + ((melMax - melMin) * i / (nMels + 1));
        }

        float[] hzPoints = melPoints.Select(MelToHz).ToArray();
        var bins = hzPoints.Select(hz => (int)Math.Floor((nFft + 1) * hz / sampleRate)).ToArray();
        var filterBank = new float[nMels, nFreqs];
        for (int m = 0; m < nMels; m++)
        {
            int left = bins[m];
            int center = bins[m + 1];
            int right = bins[m + 2];
            for (int k = left; k < center && k < nFreqs; k++)
            {
                filterBank[m, k] = (k - left) / (float)Math.Max(1, center - left);
            }

            for (int k = center; k < right && k < nFreqs; k++)
            {
                filterBank[m, k] = (right - k) / (float)Math.Max(1, right - center);
            }
        }

        return filterBank;
    }

    private static float[,] BuildWhisperMelFilters(int nMels, int nFreqs, int sampleRate, int nFft)
    {
        // Slaney mel for Whisper (same as LatentSync 80-bin path, extended to 128 bins).
        _ = LatentSyncTensorPreprocessor.MelShape;
        return BuildMelFilterBank(nMels, nFreqs, sampleRate, 0f, sampleRate / 2f, nFft);
    }

    private static void FftInPlace(double[] real, double[] imag)
    {
        int n = real.Length;
        if (n <= 1)
        {
            return;
        }

        var complex = new Complex[n];
        for (int i = 0; i < n; i++)
        {
            complex[i] = new Complex(real[i], imag[i]);
        }

        Fourier.Forward(complex, FourierOptions.Matlab);
        for (int i = 0; i < n; i++)
        {
            real[i] = complex[i].Real;
            imag[i] = complex[i].Imaginary;
        }
    }
}
