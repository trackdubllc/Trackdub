namespace Trackdub.Media.Mixing;

internal static class SpectralEnvelopeMatcher
{
    public const float MaxGainDb = 12f;

    private const float MinWet = 0.25f;
    private const float MaxWet = 0.78f;
    private const float LsdLow = 0.3f;
    private const float LsdHigh = 2.5f;
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

        // Center-pad the TTS signal so ISTFT has full Hann coverage at both boundaries.
        int pad = stft.FftSize / 2;
        float[] paddedTts = CenterPad(ttsSamples, pad);

        // Single Forward pass for TTS; reuse frames for both envelope extraction and shaping.
        System.Numerics.Complex[][] ttsFrames = stft.Forward(paddedTts);
        SpectralEnvelope? ttsResult = SpectralEnvelopeAnalyzer.ExtractFromFrames(ttsFrames, paddedTts, stft.HopSize);
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

        for (int frameIndex = 0; frameIndex < ttsFrames.Length; frameIndex++)
        {
            System.Numerics.Complex[] frame = ttsFrames[frameIndex];
            for (int k = 0; k < frame.Length; k++)
                frame[k] *= transferFn[k];
        }

        float[] paddedMatched = stft.Inverse(ttsFrames, paddedTts.Length);

        float dry = 1f - wet;
        var result = new float[ttsSamples.Length];
        for (int i = 0; i < result.Length; i++)
            result[i] = (ttsSamples[i] * dry) + (paddedMatched[pad + i] * wet);
        return result;
    }

    private static float ComputeWetRatio(float[] ttsEnv, float[] refEnv, float refVoicedRatio)
    {
        double sumSq = 0d;
        for (int k = 0; k < ttsEnv.Length; k++)
        {
            double diff = Math.Log(refEnv[k] + Epsilon) - Math.Log(ttsEnv[k] + Epsilon);
            sumSq += diff * diff;
        }
        float lsd = (float)Math.Sqrt(sumSq / ttsEnv.Length);

        float t = Math.Clamp((lsd - LsdLow) / (LsdHigh - LsdLow), 0f, 1f);
        float wet = MinWet + (t * (MaxWet - MinWet));

        if (refVoicedRatio < MinConfidentVoicedRatio)
            wet = Math.Min(wet, MinWet + ((MaxWet - MinWet) * (refVoicedRatio / MinConfidentVoicedRatio)));

        return wet;
    }

    private static float[] CenterPad(ReadOnlySpan<float> samples, int padSize)
    {
        var result = new float[samples.Length + (2 * padSize)];
        samples.CopyTo(result.AsSpan(padSize));
        return result;
    }
}
