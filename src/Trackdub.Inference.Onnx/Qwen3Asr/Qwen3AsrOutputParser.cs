using System.Text.RegularExpressions;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal static class Qwen3AsrOutputParser
{
    private static readonly Regex RepeatedCharPattern = new(@"(.)\1{5,}", RegexOptions.Compiled);

    // Matches Qwen chat/control markers such as <|im_end|>, <|endoftext|>, <|im_start|>.
    // The greedy decoder appends the terminal EOS token, and tokenizer.Decode renders
    // special tokens as literal text, so without this they leak onto the end of every
    // segment (especially on the forced-language path, which returns the decode verbatim).
    private static readonly Regex SpecialTokenPattern = new(@"<\|[^|>]*\|>", RegexOptions.Compiled);

    public static (string LanguageName, string Text) Parse(string raw, string? forcedLanguageName = null)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return ("", "");
        }

        string normalized = DetectAndFixRepetitions(raw.Trim());
        if (!string.IsNullOrWhiteSpace(forcedLanguageName))
        {
            return (Qwen3AsrLanguageCodes.NormalizeLanguageName(forcedLanguageName), StripSpecialTokens(normalized));
        }

        if (!normalized.Contains(Qwen3AsrPromptTokens.AsrTextTag, StringComparison.Ordinal))
        {
            return ("", StripSpecialTokens(normalized));
        }

        string[] parts = normalized.Split(Qwen3AsrPromptTokens.AsrTextTag, 2, StringSplitOptions.None);
        string metaPart = parts[0];
        string textPart = parts.Length > 1 ? StripSpecialTokens(parts[1]) : "";

        if (metaPart.Contains("language none", StringComparison.OrdinalIgnoreCase) && string.IsNullOrWhiteSpace(textPart))
        {
            return ("", "");
        }

        foreach (string line in metaPart.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (line.StartsWith(Qwen3AsrPromptTokens.LanguagePrefix, StringComparison.OrdinalIgnoreCase))
            {
                string value = line[Qwen3AsrPromptTokens.LanguagePrefix.Length..].Trim();
                if (value.Length > 0)
                {
                    return (Qwen3AsrLanguageCodes.NormalizeLanguageName(value), textPart);
                }
            }
        }

        return ("", textPart);
    }

    private static string StripSpecialTokens(string text) =>
        SpecialTokenPattern.Replace(text, string.Empty).Trim();

    internal static string DetectAndFixRepetitionsForTesting(string text) => DetectAndFixRepetitions(text);

    private static string DetectAndFixRepetitions(string text)
    {
        if (text.Length < 12)
        {
            return text;
        }

        return RepeatedCharPattern.Replace(text, "$1$1$1");
    }
}
