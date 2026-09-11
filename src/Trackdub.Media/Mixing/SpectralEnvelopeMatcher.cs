namespace Trackdub.Media.Mixing;

internal static class SpectralEnvelopeMatcher
{
    public const float MaxGainDb = 12f;

    // Wet ratio bounds: small LSD (close match) → gentle touch; large LSD (big mismatch) → aggressive
    private const float MinWet = 0.25f;
    private const float MaxWet = 0.78f;

    // LSD range (in log units) that maps MinWet → MaxWet
    private const float LsdLow = 0.3f;
    private const float LsdHigh = 2.5f;

    // Reference confidence: below this voiced-frame ratio the estimate is unreliable → cap wet
    private const float MinConfidentVoicedRatio = 0.25f;

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

        SpectralEnvelope? ttsResult = SpectralEnvelopeAnalyzer.Extract(ttsSamples, stft);
        SpectralEnvelope? refResult = SpectralEnvelopeAnalyzer.Extract(referenceSamples, stft);

        if (ttsResult is null || refResult is null)
            return null;

        float[] ttsEnv = ttsResult.Value.Bins;
        float[] refEnv = refResult.Value.Bins;

        float wet = ComputeWetRatio(ttsEnv, refEnv, refResult.Value.VoicedFrameRatio);

        var transferFn = new float[stft.BinCount];
        for (int k = 0; k < stft.BinCount; k++)
        {
            float h = refEnv[k] / (ttsEnv[k] + Epsilon);
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
        float dry = 1f - wet;

        var result = new float[ttsSamples.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = ttsSamples[i] * dry + matched[i] * wet;
        return result;
    }

    private static float ComputeWetRatio(float[] ttsEnv, float[] refEnv, float refVoicedRatio)
    {
        // Log-spectral distance between the two envelopes
        double sumSq = 0d;
        for (int k = 0; k < ttsEnv.Length; k++)
        {
            double diff = Math.Log(refEnv[k] + Epsilon) - Math.Log(ttsEnv[k] + Epsilon);
            sumSq += diff * diff;
        }
        float lsd = (float)Math.Sqrt(sumSq / ttsEnv.Length);

        // Linear ramp: LsdLow → MinWet, LsdHigh → MaxWet
        float t = Math.Clamp((lsd - LsdLow) / (LsdHigh - LsdLow), 0f, 1f);
        float wet = MinWet + t * (MaxWet - MinWet);

        // Cap when reference envelope estimate is unreliable (too few voiced frames)
        if (refVoicedRatio < MinConfidentVoicedRatio)
            wet = Math.Min(wet, MinWet + (MaxWet - MinWet) * (refVoicedRatio / MinConfidentVoicedRatio));

        return wet;
    }
}
