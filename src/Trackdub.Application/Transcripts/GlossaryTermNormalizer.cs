using System.Globalization;
using System.Text;

namespace Trackdub.Application.Transcripts;

internal sealed class GlossaryTermNormalizer
{
    private static readonly HashSet<string> LatinSourceLanguages = new(StringComparer.Ordinal)
    {
        "en",
        "es",
        "fr",
        "de",
        "it",
        "pt"
    };

    private static readonly HashSet<string> CjkSourceLanguages = new(StringComparer.Ordinal)
    {
        "ja",
        "zh",
        "zh-hans",
        "zh-hant",
        "ko"
    };

    public bool SupportsSourceLanguage(string sourceLanguage) =>
        GetLanguageKind(sourceLanguage) != GlossaryLanguageKind.Unsupported;

    public NormalizedGlossaryText Normalize(
        string sourceLanguage,
        string text,
        bool isCaseSensitive)
    {
        GlossaryLanguageKind languageKind = GetLanguageKind(sourceLanguage);
        if (string.IsNullOrEmpty(text))
        {
            return new NormalizedGlossaryText([], []);
        }

        var originalTextElements = new List<string>();
        var normalizedTextElements = new List<NormalizedGlossaryTextElement>();
        TextElementEnumerator enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            string originalTextElement = enumerator.GetTextElement();
            int originalTextElementIndex = originalTextElements.Count;
            originalTextElements.Add(originalTextElement);

            string normalizedTextElement = NormalizeTextElement(
                languageKind,
                originalTextElement,
                isCaseSensitive);
            if (normalizedTextElement.Length == 0)
            {
                continue;
            }

            normalizedTextElements.Add(new NormalizedGlossaryTextElement(
                normalizedTextElement,
                originalTextElementIndex,
                OriginalTextElementLength: 1));
        }

        return new NormalizedGlossaryText(originalTextElements, normalizedTextElements);
    }

    private static GlossaryLanguageKind GetLanguageKind(string sourceLanguage)
    {
        if (string.IsNullOrWhiteSpace(sourceLanguage))
        {
            return GlossaryLanguageKind.Unsupported;
        }

        string normalizedLanguage = sourceLanguage.Trim().ToLowerInvariant();
        if (LatinSourceLanguages.Contains(normalizedLanguage))
        {
            return GlossaryLanguageKind.Latin;
        }

        if (CjkSourceLanguages.Contains(normalizedLanguage))
        {
            return normalizedLanguage == "ja"
                ? GlossaryLanguageKind.Japanese
                : GlossaryLanguageKind.Cjk;
        }

        return normalizedLanguage == "ar"
            ? GlossaryLanguageKind.Arabic
            : GlossaryLanguageKind.Unsupported;
    }

    private static string NormalizeTextElement(
        GlossaryLanguageKind languageKind,
        string textElement,
        bool isCaseSensitive) =>
        languageKind switch
        {
            GlossaryLanguageKind.Latin => NormalizeLatinTextElement(textElement, isCaseSensitive),
            GlossaryLanguageKind.Japanese => NormalizeCjkTextElement(textElement, isCaseSensitive, foldKatakanaToHiragana: true),
            GlossaryLanguageKind.Cjk => NormalizeCjkTextElement(textElement, isCaseSensitive, foldKatakanaToHiragana: false),
            GlossaryLanguageKind.Arabic => NormalizeArabicTextElement(textElement),
            _ => textElement
        };

    private static string NormalizeLatinTextElement(
        string textElement,
        bool isCaseSensitive)
    {
        var punctuationNormalized = new StringBuilder(textElement.Length);
        foreach (char character in textElement)
        {
            punctuationNormalized.Append(NormalizeCommonPunctuation(character));
        }

        string decomposed = punctuationNormalized.ToString().Normalize(NormalizationForm.FormD);
        var accentFolded = new StringBuilder(decomposed.Length);
        foreach (char character in decomposed)
        {
            UnicodeCategory category = CharUnicodeInfo.GetUnicodeCategory(character);
            if (category is UnicodeCategory.NonSpacingMark
                or UnicodeCategory.SpacingCombiningMark
                or UnicodeCategory.EnclosingMark)
            {
                continue;
            }

            accentFolded.Append(character);
        }

        string normalized = accentFolded.ToString().Normalize(NormalizationForm.FormC);
        return isCaseSensitive
            ? normalized
            : normalized.ToLowerInvariant();
    }

    private static string NormalizeCjkTextElement(
        string textElement,
        bool isCaseSensitive,
        bool foldKatakanaToHiragana)
    {
        string normalized = textElement.Normalize(NormalizationForm.FormKC);
        if (foldKatakanaToHiragana)
        {
            normalized = FoldKatakanaToHiragana(normalized);
        }

        return isCaseSensitive
            ? normalized
            : normalized.ToLowerInvariant();
    }

    private static string NormalizeArabicTextElement(string textElement)
    {
        var normalized = new StringBuilder(textElement.Length);
        foreach (char character in textElement)
        {
            if (character == '\u0640' || IsArabicMark(character))
            {
                continue;
            }

            normalized.Append(character switch
            {
                '\u0622' or '\u0623' or '\u0625' or '\u0671' or '\u0672' or '\u0673' or '\u0675' => '\u0627',
                '\u0649' => '\u064A',
                _ => character
            });
        }

        return normalized.ToString();
    }

    private static char NormalizeCommonPunctuation(char character) =>
        character switch
        {
            '\u2018' or '\u2019' or '\u201B' or '\u2032' or '\u02BC' => '\'',
            '\u201C' or '\u201D' or '\u201E' or '\u2033' => '"',
            '\u2010' or '\u2011' or '\u2012' or '\u2013' or '\u2014' or '\u2212' => '-',
            _ => character
        };

    private static string FoldKatakanaToHiragana(string text)
    {
        var folded = new StringBuilder(text.Length);
        foreach (char character in text)
        {
            folded.Append(character is >= '\u30A1' and <= '\u30F6'
                ? (char)(character - 0x60)
                : character);
        }

        return folded.ToString();
    }

    private static bool IsArabicMark(char character) =>
        character is >= '\u0610' and <= '\u061A'
            or >= '\u064B' and <= '\u065F'
            or '\u0670'
            or >= '\u06D6' and <= '\u06ED'
            or >= '\u08D3' and <= '\u08FF';

    private enum GlossaryLanguageKind
    {
        Unsupported,
        Latin,
        Japanese,
        Cjk,
        Arabic
    }
}

internal sealed record NormalizedGlossaryText(
    IReadOnlyList<string> OriginalTextElements,
    IReadOnlyList<NormalizedGlossaryTextElement> TextElements);

internal sealed record NormalizedGlossaryTextElement(
    string Value,
    int OriginalStartTextElementIndex,
    int OriginalTextElementLength);
