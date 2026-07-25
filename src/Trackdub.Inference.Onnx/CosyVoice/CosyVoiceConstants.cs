namespace Trackdub.Inference.Onnx.CosyVoice;

internal static class CosyVoiceConstants
{
    public const int SampleRate = 22_050;
    public const int SpeechTokenSize = 4096;
    public const int EosToken = 4096;
    public const int SosToken = 0;
    public const int TaskIdToken = 1;
    public const int InputFrameRate = 50;
    public const int LlmHiddenSize = 1024;
    public const int FlowTokenEmbedDim = 512;
    public const int MelBins = 80;
    public const int MelHop = 256;
    public const int CampplusSampleRate = 16_000;
    public const int SpeechTokenizerSampleRate = 16_000;
    public const int SpeechTokenizerMelBins = 128;
    public const int F0UpsampleFactor = 256;
    public const int CfmSteps = 10;
    public const float CfmCfgRate = 0.7f;
    public const int DefaultSamplingTopK = 25;
    public const float MinTokenTextRatio = 2f;
    public const float MaxTokenTextRatio = 20f;
    public const string WhisperTokenPattern =
        @"'s|'t|'re|'ve|'m|'ll|'d| ?\p{L}+| ?\p{N}+| ?[^\s\p{L}\p{N}]+|\s+(?!\S)|\s+";
}
