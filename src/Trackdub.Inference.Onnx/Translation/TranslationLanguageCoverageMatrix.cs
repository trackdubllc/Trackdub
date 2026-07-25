namespace Trackdub.Inference.Onnx.Translation;

internal sealed record TranslationLanguageDefinition(
    string Code,
    string DisplayName,
    string MadladTag);

internal static class TranslationLanguageCoverageMatrix
{
    // Coverage tracks the Chatterbox multilingual TTS language set (22 languages).
    // `MadladTag` is the token MADLAD-400 expects in its <2xx> target prefix; it was
    // verified against the model's SentencePiece vocab (jbochi/madlad400-3b-mt/spiece.model)
    // and equals the 2-letter ISO 639-1 code for every language in this set. Do NOT use
    // 3-letter codes here: MADLAD's vocab has no <2eng>/<2por>/... tokens, so a 3-letter
    // tag silently degrades translation to an unknown-token prefix.
    //
    // Chinese (zh) is intentionally excluded: it is disabled in the Chatterbox multilingual
    // model, so advertising it for dubbing would fake TTS readiness.
    private static readonly TranslationLanguageDefinition[] Definitions =
    [
        new("ar", "Arabic", "ar"),
        new("da", "Danish", "da"),
        new("de", "German", "de"),
        new("el", "Greek", "el"),
        new("en", "English", "en"),
        new("es", "Spanish", "es"),
        new("fi", "Finnish", "fi"),
        new("fr", "French", "fr"),
        new("he", "Hebrew", "he"),
        new("hi", "Hindi", "hi"),
        new("it", "Italian", "it"),
        new("ja", "Japanese", "ja"),
        new("ko", "Korean", "ko"),
        new("ms", "Malay", "ms"),
        new("nl", "Dutch", "nl"),
        new("no", "Norwegian", "no"),
        new("pl", "Polish", "pl"),
        new("pt", "Portuguese", "pt"),
        new("ru", "Russian", "ru"),
        new("sv", "Swedish", "sv"),
        new("sw", "Swahili", "sw"),
        new("tr", "Turkish", "tr"),
    ];

    private static readonly IReadOnlyDictionary<string, TranslationLanguageDefinition> Languages =
        Definitions.ToDictionary(
            definition => definition.Code,
            definition => definition,
            StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<TranslationLanguageDefinition> GetTargets(string sourceLanguage)
    {
        string normalizedSource = Normalize(sourceLanguage)
            ?? throw new InvalidOperationException("Source language is required.");

        return Languages.ContainsKey(normalizedSource)
            ? AllLanguages
                .Where(language => !string.Equals(language.Code, normalizedSource, StringComparison.OrdinalIgnoreCase))
                .ToArray()
            : [];
    }

    public static IReadOnlyList<TranslationLanguageDefinition> AllLanguages { get; } =
        Languages.Values
            .OrderBy(language => language.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public static bool TryGetLanguage(string? languageCode, out TranslationLanguageDefinition? definition)
    {
        if (Normalize(languageCode) is not string normalizedLanguageCode)
        {
            definition = null;
            return false;
        }

        return Languages.TryGetValue(normalizedLanguageCode, out definition);
    }

    private static string? Normalize(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();
}
