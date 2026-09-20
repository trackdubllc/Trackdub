namespace Trackdub.Inference.Onnx.Spleeter;

/// <summary>
/// Shared constants and soft-mask math for the sherpa-onnx Spleeter 2stems path.
/// Production separator/engine and parity tests must use these so contracts cannot drift.
/// </summary>
internal static class SpleeterModelConstants
{
    public const string VocalsModelFileName = "vocals.onnx";
    public const string AccompanimentModelFileName = "accompaniment.onnx";
    public const int TargetSampleRate = 44100;

    /// <summary>STFT size (sherpa-onnx / Deezer 2stems).</summary>
    public const int Nfft = 4096;

    /// <summary>STFT hop (sherpa-onnx / Deezer 2stems).</summary>
    public const int Hop = 1024;

    /// <summary>Frequency bins kept for the ONNX UNet (first half of rFFT usable band for this export).</summary>
    public const int MaxFreqBins = 1024;

    /// <summary>Time-frame pad / ONNX chunk height (num_splits granularity).</summary>
    public const int TimePad = 512;

    public const float MaskEpsilon = 1e-10f;

    /// <summary>
    /// Frame count after sherpa-onnx-style time padding:
    /// <c>padding = TimePad - (baseFrames % TimePad)</c> applied when positive.
    /// An exact multiple still receives another full pad block (matches separate_onnx.py).
    /// </summary>
    public static int PadTimeFrames(int baseFrames)
    {
        if (baseFrames <= 0)
        {
            return TimePad;
        }

        int remainder = baseFrames % TimePad;
        int padding = TimePad - remainder;
        return padding > 0 ? baseFrames + padding : baseFrames;
    }

    /// <summary>
    /// Soft mask used by <see cref="SpleeterOnnxSeparator"/>:
    /// <c>stem² / (vocals² + accompaniment² + ε)</c>.
    /// </summary>
    public static void ComputeSoftMasks(
        float vocalsMagnitude,
        float accompanimentMagnitude,
        out float maskVocals,
        out float maskAccompaniment)
    {
        float denom = (vocalsMagnitude * vocalsMagnitude)
            + (accompanimentMagnitude * accompanimentMagnitude)
            + MaskEpsilon;
        maskVocals = (vocalsMagnitude * vocalsMagnitude) / denom;
        maskAccompaniment = (accompanimentMagnitude * accompanimentMagnitude) / denom;
    }
}
