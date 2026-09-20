namespace Trackdub.Inference.Onnx.Spleeter;

/// <summary>
/// Shared constants and soft-mask math for the sherpa-onnx Spleeter 2stems path.
/// Production separator/engine and parity tests must use these so contracts cannot drift.
/// Parity target: k2-fsa/sherpa-onnx <c>scripts/spleeter/separate_onnx.py</c>.
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

    /// <summary>Frequency bins kept for the ONNX UNet (export keeps the first 1024 bins).</summary>
    public const int MaxFreqBins = 1024;

    /// <summary>Time-frame pad / ONNX chunk height (num_splits granularity).</summary>
    public const int TimePad = 512;

    public const float MaskEpsilon = 1e-10f;

    /// <summary>
    /// Frame count after sherpa-onnx time padding:
    /// <c>padding = TimePad - (baseFrames % TimePad)</c> (always in 1..TimePad for baseFrames&gt;0).
    /// Exact multiples still receive another full pad block (matches separate_onnx.py).
    /// </summary>
    public static int PadTimeFrames(int baseFrames)
    {
        if (baseFrames <= 0)
        {
            return TimePad;
        }

        int remainder = baseFrames % TimePad;
        return baseFrames + (TimePad - remainder);
    }

    /// <summary>
    /// Soft mask matching sherpa-onnx separate_onnx.py:
    /// <c>(stem² + ε/2) / (vocals² + accompaniment² + ε)</c>.
    /// Vocals + accomp masks sum to 1 in exact arithmetic.
    /// </summary>
    public static void ComputeSoftMasks(
        float vocalsMagnitude,
        float accompanimentMagnitude,
        out float maskVocals,
        out float maskAccompaniment)
    {
        float halfEps = MaskEpsilon / 2f;
        float denom = (vocalsMagnitude * vocalsMagnitude)
            + (accompanimentMagnitude * accompanimentMagnitude)
            + MaskEpsilon;
        maskVocals = ((vocalsMagnitude * vocalsMagnitude) + halfEps) / denom;
        maskAccompaniment = ((accompanimentMagnitude * accompanimentMagnitude) + halfEps) / denom;
    }

    /// <summary>
    /// Builds a model path under <paramref name="modelRootPath"/> for a known
    /// Spleeter 2stems file name only. Rejects rooted names and any name that is
    /// not one of the two shipping ONNX files (blocks <c>../</c> traversal).
    /// </summary>
    public static string ResolveModelPath(string modelRootPath, string modelFileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelRootPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelFileName);
        if (Path.IsPathRooted(modelFileName))
        {
            throw new ArgumentException(
                $"Spleeter model file name must be relative, but '{modelFileName}' is rooted.",
                nameof(modelFileName));
        }

        bool known = string.Equals(modelFileName, VocalsModelFileName, StringComparison.Ordinal)
            || string.Equals(modelFileName, AccompanimentModelFileName, StringComparison.Ordinal);
        if (!known)
        {
            throw new ArgumentException(
                $"Unknown Spleeter model file name '{modelFileName}'. Expected '{VocalsModelFileName}' or '{AccompanimentModelFileName}'.",
                nameof(modelFileName));
        }

        string combined = Path.Combine(modelRootPath, modelFileName);
        string rootFull = Path.GetFullPath(modelRootPath);
        string resolvedFull = Path.GetFullPath(combined);
        string rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar) || rootFull.EndsWith(Path.AltDirectorySeparatorChar)
            ? rootFull
            : rootFull + Path.DirectorySeparatorChar;
        if (!resolvedFull.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Resolved model path '{resolvedFull}' escapes model root '{rootFull}'.",
                nameof(modelFileName));
        }

        return combined;
    }
}
