namespace Trackdub.Media.Mixing;

internal static class RoomToneConvolver
{
    private const int MaxIrSamples = 1024;
    private const int MinIrSamples = 128;
    private const float PreRollRmsThreshold = 0.0005f;
    private const float WetRatio = 0.20f;
    private const float DryRatio = 1f - WetRatio;
    // Treat IR as silent when L1 norm is below this threshold (~-140 dBFS per sample relative to 1.0).
    private const float L1NormSilenceThreshold = 1e-6f;

    // Returns null when reverb cannot be applied; caller uses the dry signal as-is.
    public static float[]? TryApply(ReadOnlySpan<float> input, ReadOnlySpan<float> preRoll)
    {
        if (input.IsEmpty || preRoll.Length < MinIrSamples)
        {
            return null;
        }

        // Use the freshest portion of the pre-roll — the tail immediately before the segment.
        ReadOnlySpan<float> ir = preRoll.Length > MaxIrSamples
            ? preRoll.Slice(preRoll.Length - MaxIrSamples)
            : preRoll;

        // Reject silent pre-roll — nothing useful to impart.
        double sumSquares = 0d;
        for (int i = 0; i < ir.Length; i++)
        {
            sumSquares += ir[i] * (double)ir[i];
        }

        float rms = (float)Math.Sqrt(sumSquares / ir.Length);
        if (rms < PreRollRmsThreshold)
        {
            return null;
        }

        // Normalize by L1 norm so the convolution preserves the input amplitude.
        float l1Norm = 0f;
        for (int i = 0; i < ir.Length; i++)
        {
            l1Norm += Math.Abs(ir[i]);
        }

        if (l1Norm <= L1NormSilenceThreshold)
        {
            return null;
        }

        float[] irNormalized = new float[ir.Length];
        float irScale = 1f / l1Norm;
        for (int i = 0; i < ir.Length; i++)
        {
            irNormalized[i] = ir[i] * irScale;
        }

        // Short fade-in prevents a transient click at the convolution boundary.
        int fadeSamples = Math.Min(32, ir.Length / 4);
        for (int i = 0; i < fadeSamples; i++)
        {
            irNormalized[i] *= (float)i / fadeSamples;
        }

        // Direct time-domain convolution — outer loop over IR coefficients lets the JIT
        // vectorize the inner scatter-accumulate (constant irCoeff, sequential memory access).
        float[] convolved = new float[input.Length];
        for (int j = 0; j < irNormalized.Length; j++)
        {
            float irCoeff = irNormalized[j];
            int limit = input.Length - j;
            for (int k = 0; k < limit; k++)
            {
                convolved[k + j] += irCoeff * input[k];
            }
        }

        // Wet/dry blend.
        float[] result = new float[input.Length];
        for (int i = 0; i < input.Length; i++)
        {
            result[i] = input[i] * DryRatio + convolved[i] * WetRatio;
        }

        return result;
    }
}
