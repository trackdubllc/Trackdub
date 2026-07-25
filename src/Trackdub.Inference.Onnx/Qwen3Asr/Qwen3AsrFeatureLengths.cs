namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal static class Qwen3AsrFeatureLengths
{
    private const int ConvWindow = 100;
    private const int TokensPerWindow = 13;

    private static int ConvOutLen(int frameCount) => (frameCount + 1) / 2;

    /// <summary>
    /// Encoder output token count from mel frame count (matches Qwen3-ASR ONNX export).
    /// </summary>
    public static int GetEncoderOutputLength(int melFrameCount)
    {
        if (melFrameCount <= 0)
        {
            return 0;
        }

        int leave = melFrameCount % ConvWindow;
        int tokens = ConvOutLen(leave);
        tokens = ConvOutLen(tokens);
        tokens = ConvOutLen(tokens);
        return tokens + ((melFrameCount / ConvWindow) * TokensPerWindow);
    }
}
