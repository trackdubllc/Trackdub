namespace Trackdub.Media.Mixing;

/// <summary>
/// Mild synthetic room impulse used when segment pre-roll cannot supply a usable IR.
/// Curated OpenSLR SLR28 RIRs could replace or augment this bank later without changing call sites.
/// </summary>
internal static class RoomToneFallbackImpulse
{
    private const int Length = 512;
    private static readonly Lazy<float[]> Cached = new(CreateNormalizedImpulse);

    public static ReadOnlySpan<float> Samples => Cached.Value;

    private static float[] CreateNormalizedImpulse()
    {
        float[] impulse = new float[Length];
        for (int i = 0; i < Length; i++)
        {
            float progress = i / (float)(Length - 1);
            float envelope = MathF.Exp(-3.5f * progress);
            float tone = MathF.Sin((2f * MathF.PI * 2.3f * i) / Length);
            impulse[i] = envelope * tone;
        }

        float l1Norm = 0f;
        for (int i = 0; i < impulse.Length; i++)
        {
            l1Norm += MathF.Abs(impulse[i]);
        }

        if (l1Norm <= 0f)
        {
            return impulse;
        }

        float scale = 1f / l1Norm;
        for (int i = 0; i < impulse.Length; i++)
        {
            impulse[i] *= scale;
        }

        int fadeSamples = Math.Min(32, Length / 4);
        for (int i = 0; i < fadeSamples; i++)
        {
            impulse[i] *= i / (float)fadeSamples;
        }

        return impulse;
    }
}
