namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal static class Qwen3AsrPromptBuilder
{
    public static IReadOnlyList<int> BuildPromptIds(int audioTokenCount, IReadOnlyList<int>? forcedLanguageSuffix = null)
    {
        if (audioTokenCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(audioTokenCount));
        }

        var ids = new List<int>
        {
            Qwen3AsrPromptTokens.ImStartTokenId,
            9125,
            Qwen3AsrPromptTokens.NewlineTokenId,
            Qwen3AsrPromptTokens.ImEndTokenId,
            Qwen3AsrPromptTokens.NewlineTokenId,
            Qwen3AsrPromptTokens.ImStartTokenId,
            882,
            Qwen3AsrPromptTokens.NewlineTokenId,
            Qwen3AsrPromptTokens.AudioStartTokenId,
        };

        for (int index = 0; index < audioTokenCount; index++)
        {
            ids.Add(Qwen3AsrPromptTokens.AudioPadTokenId);
        }

        ids.Add(Qwen3AsrPromptTokens.AudioEndTokenId);
        ids.Add(Qwen3AsrPromptTokens.ImEndTokenId);
        ids.Add(Qwen3AsrPromptTokens.NewlineTokenId);
        ids.Add(Qwen3AsrPromptTokens.ImStartTokenId);
        ids.Add(77091);
        ids.Add(Qwen3AsrPromptTokens.NewlineTokenId);

        if (forcedLanguageSuffix is { Count: > 0 })
        {
            ids.AddRange(forcedLanguageSuffix);
        }

        return ids;
    }

    public static (int Start, int End) GetAudioPadRange(IReadOnlyList<int> promptIds)
    {
        int start = -1;
        int end = -1;
        for (int index = 0; index < promptIds.Count; index++)
        {
            if (promptIds[index] != Qwen3AsrPromptTokens.AudioPadTokenId)
            {
                continue;
            }

            if (start < 0)
            {
                start = index;
            }

            end = index + 1;
        }

        if (start < 0 || end <= start)
        {
            throw new InvalidOperationException("Prompt does not contain <|audio_pad|> tokens.");
        }

        return (start, end);
    }

    public static IReadOnlyList<int> BuildForcedLanguageSuffix(Qwen3AsrTokenizer tokenizer, string languageName)
    {
        ArgumentNullException.ThrowIfNull(tokenizer);
        ArgumentException.ThrowIfNullOrWhiteSpace(languageName);
        string normalized = Qwen3AsrLanguageCodes.NormalizeLanguageName(languageName);
        var suffix = new List<int>(tokenizer.Encode($"{Qwen3AsrPromptTokens.LanguagePrefix}{normalized}"));
        suffix.Add(Qwen3AsrPromptTokens.AsrTextTokenId);
        return suffix;
    }
}
