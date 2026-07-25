namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal static class Qwen3AsrLanguageCodes
{
    private static readonly IReadOnlyDictionary<string, string> IsoToLanguageName =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["en"] = "English",
            ["zh"] = "Chinese",
            ["yue"] = "Cantonese",
            ["ar"] = "Arabic",
            ["de"] = "German",
            ["fr"] = "French",
            ["es"] = "Spanish",
            ["pt"] = "Portuguese",
            ["id"] = "Indonesian",
            ["it"] = "Italian",
            ["ja"] = "Japanese",
            ["ko"] = "Korean",
            ["ru"] = "Russian",
            ["th"] = "Thai",
            ["vi"] = "Vietnamese",
            ["tr"] = "Turkish",
            ["hi"] = "Hindi",
            ["ms"] = "Malay",
            ["nl"] = "Dutch",
            ["sv"] = "Swedish",
            ["da"] = "Danish",
            ["fi"] = "Finnish",
            ["pl"] = "Polish",
            ["cs"] = "Czech",
            ["tl"] = "Filipino",
            ["fa"] = "Persian",
            ["el"] = "Greek",
            ["ro"] = "Romanian",
            ["hu"] = "Hungarian",
            ["mk"] = "Macedonian",
        };

    private static readonly IReadOnlyDictionary<string, string> LanguageNameToIso =
        IsoToLanguageName.ToDictionary(
            static pair => pair.Value,
            static pair => pair.Key,
            StringComparer.OrdinalIgnoreCase);

    public static string NormalizeLanguageName(string language)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(language);
        string trimmed = language.Trim();
        return trimmed.Length switch
        {
            0 => throw new ArgumentException("Language is empty.", nameof(language)),
            1 => trimmed.ToUpperInvariant(),
            _ => char.ToUpperInvariant(trimmed[0]) + trimmed[1..].ToLowerInvariant()
        };
    }

    public static string? TryGetLanguageName(string? isoLanguageCode)
    {
        if (string.IsNullOrWhiteSpace(isoLanguageCode))
        {
            return null;
        }

        string normalized = isoLanguageCode.Trim().ToLowerInvariant();
        return IsoToLanguageName.TryGetValue(normalized, out string? languageName)
            ? languageName
            : null;
    }

    public static string? TryGetIsoCode(string? languageName)
    {
        if (string.IsNullOrWhiteSpace(languageName))
        {
            return null;
        }

        string normalized = NormalizeLanguageName(languageName);
        return LanguageNameToIso.TryGetValue(normalized, out string? isoCode)
            ? isoCode
            : null;
    }
}
