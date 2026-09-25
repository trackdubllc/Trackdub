using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx;

namespace Trackdub.Inference.Onnx.ForcedAlignment;

/// <summary>
/// Converts 16 kHz mono PCM float32 audio to the log-mel spectrogram expected by
/// Qwen3-ForcedAligner, matching the parameters in its <c>preprocessor_config.json</c>:
/// 128 mel bins, a 400-sample FFT, a 160-sample hop, and 3,000 frames.
/// </summary>
internal sealed class QwenAudioFeatureExtractor
{
    private const int SampleRate = 16_000;
    private const int FftSize = 400;
    private const int HopLength = 160;
    private const int MelBins = 128;
    private const int MaxSamples = 480_000;
    private const int MaxFrames = 3_000;

    private readonly float[] hannWindow = QwenMelSpectrogramMath.BuildPeriodicHannWindow(FftSize);
    private readonly float[,] melFilters = QwenMelSpectrogramMath.BuildMelFilterBank(SampleRate, FftSize, MelBins);

    /// <summary>Extracts the normalized log-mel spectrogram, padding or trimming audio to 30 seconds.</summary>
    public DenseTensor<float> Extract(ReadOnlySpan<float> inputSamples)
    {
        float[]? rentedPaddedOrTrimmed = null;
        float[]? rentedPaddedForStft = null;
        try
        {
            ReadOnlySpan<float> paddedOrTrimmed = PadOrTrim(inputSamples, MaxSamples, out rentedPaddedOrTrimmed);
            ReadOnlySpan<float> paddedForStft = QwenMelSpectrogramMath.ReflectPad(
                paddedOrTrimmed, FftSize / 2, out rentedPaddedForStft);
            int frameCount = Math.Min(
                MaxFrames,
                1 + ((paddedForStft.Length - FftSize) / HopLength));
            float[,] powerSpectrum = QwenMelSpectrogramMath.ComputePowerSpectrum(
                paddedForStft, frameCount, FftSize, HopLength, hannWindow);
            float[,] melSpectrum = QwenMelSpectrogramMath.ApplyMelFilters(powerSpectrum, melFilters);
            QwenMelSpectrogramMath.NormalizeLogMel(melSpectrum);

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
            if (rentedPaddedOrTrimmed is not null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(rentedPaddedOrTrimmed);
            }

            if (rentedPaddedForStft is not null)
            {
                System.Buffers.ArrayPool<float>.Shared.Return(rentedPaddedForStft);
            }
        }
    }

    private static ReadOnlySpan<float> PadOrTrim(
        ReadOnlySpan<float> inputSamples,
        int targetLength,
        out float[]? rentedArray)
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
}
