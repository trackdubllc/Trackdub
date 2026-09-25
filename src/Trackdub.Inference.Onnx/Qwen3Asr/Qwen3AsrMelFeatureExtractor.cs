using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

/// <summary>16 kHz log-mel spectrogram for Qwen3-ASR (128 bins, Whisper-compatible preprocessing).</summary>
internal sealed class Qwen3AsrMelFeatureExtractor
{
    private const int SampleRate = 16_000;
    private const int FftSize = 400;
    private const int HopLength = 160;
    private const int MelBins = 128;
    private const int MaxFrames = 3_000;

    private readonly float[] hannWindow = QwenMelSpectrogramMath.BuildPeriodicHannWindow(FftSize);
    private readonly float[,] melFilters = QwenMelSpectrogramMath.BuildMelFilterBank(SampleRate, FftSize, MelBins);

    public DenseTensor<float> Extract(ReadOnlySpan<float> inputSamples)
    {
        float[]? rentedPaddedForStft = null;
        try
        {
            ReadOnlySpan<float> paddedForStft = QwenMelSpectrogramMath.ReflectPad(
                inputSamples, FftSize / 2, out rentedPaddedForStft);
            // Centered STFT yields 1 + n/hop frames; WhisperFeatureExtractor drops the last one.
            int frameCount = Math.Min(
                MaxFrames,
                Math.Max(0, (paddedForStft.Length - FftSize) / HopLength));
            if (frameCount == 0)
            {
                return new DenseTensor<float>(Array.Empty<float>(), [1, MelBins, 0]);
            }

            float[,] powerSpectrum = QwenMelSpectrogramMath.ComputePowerSpectrum(
                paddedForStft, frameCount, FftSize, HopLength, hannWindow);
            float[,] melSpectrum = QwenMelSpectrogramMath.ApplyMelFilters(powerSpectrum, melFilters);
            QwenMelSpectrogramMath.NormalizeLogMel(melSpectrum);

            var data = new float[MelBins * frameCount];
            for (int melIndex = 0; melIndex < MelBins; melIndex++)
            {
                for (int frameIndex = 0; frameIndex < frameCount; frameIndex++)
                {
                    data[(melIndex * frameCount) + frameIndex] = melSpectrum[melIndex, frameIndex];
                }
            }

            return new DenseTensor<float>(data, [1, MelBins, frameCount]);
        }
        finally
        {
            if (rentedPaddedForStft is not null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(rentedPaddedForStft);
            }
        }
    }
}
