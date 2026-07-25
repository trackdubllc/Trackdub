namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal static class Qwen3AsrPromptTokens
{
    public const int EndOfTextTokenId = 151643;
    public const int ImStartTokenId = 151644;
    public const int ImEndTokenId = 151645;
    public const int AudioStartTokenId = 151669;
    public const int AudioEndTokenId = 151670;
    public const int AudioPadTokenId = 151676;
    public const int AsrTextTokenId = 151704;
    public const int NewlineTokenId = 198;

    public static readonly int[] EosTokenIds = [EndOfTextTokenId, ImEndTokenId];

    // Token id 151704 decodes to the literal "<asr_text>" (no pipes) in the Qwen3-ASR
    // tokenizer. The parser splits transcript text from the "language X" metadata on this
    // marker, so it MUST match the tokenizer's decoded form exactly.
    public const string AsrTextTag = "<asr_text>";
    public const string LanguagePrefix = "language ";
}
