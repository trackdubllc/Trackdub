namespace Trackdub.Media.Mixing;

internal static class SpectralEnvelopeMatcher
{
    public const float WetRatio = 0.55f;
    public const float DryRatio = 0.45f;
    public const float MaxGainDb = 12f;

    private static readonly float MaxLinearGain = (float)Math.Pow(10d, MaxGainDb / 20d);
    private static readonly float MinLinearGain = 1f / MaxLinearGain;
    private const float Epsilon = 1e-8f;

    public static float[]? TryApply(
        ReadOnlySpan<float> ttsSamples,
        ReadOnlySpan<float> referenceSamples,
        StftProcessor stft)
    {
        if (ttsSamples.Length < stft.FftSize || referenceSamples.Length < stft.FftSize)
            return null;

        float[]? ttsEnvelope = SpectralEnvelopeAnalyzer.Extract(ttsSamples, stft);
        float[]? refEnvelope = SpectralEnvelopeAnalyzer.Extract(referenceSamples, stft);

        if (ttsEnvelope is null || refEnvelope is null)
            return null;

        var transferFn = new float[stft.BinCount];
        for (int k = 0; k < stft.BinCount; k++)
        {
            float h = refEnvelope[k] / (ttsEnvelope[k] + Epsilon);
            transferFn[k] = Math.Clamp(h, MinLinearGain, MaxLinearGain);
        }

        System.Numerics.Complex[][] frames = stft.Forward(ttsSamples);
        for (int frameIndex = 0; frameIndex < frames.Length; frameIndex++)
        {
            System.Numerics.Complex[] frame = frames[frameIndex];
            for (int k = 0; k < frame.Length; k++)
                frame[k] *= transferFn[k];
        }

        float[] matched = stft.Inverse(frames, ttsSamples.Length);

        var result = new float[ttsSamples.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = ttsSamples[i] * DryRatio + matched[i] * WetRatio;
        return result;
    }
}
