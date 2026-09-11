using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Picks a stock Kokoro voice for speakers that cannot be cloned. Language is required.
/// Gender, unused catalog voices, and locale exactness are used when they are known.
/// </summary>
public static class StockTtsVoiceMatcher
{
    public static bool SupportsKokoro(string? languageCode)
    {
        string? normalized = NormalizeBaseLanguage(languageCode);
        return normalized is "en" or "es";
    }

    public static VoiceCatalogEntry? PickClosest(
        IReadOnlyList<VoiceCatalogEntry> voices,
        string? targetLanguageCode,
        string? gender,
        IReadOnlySet<string>? reservedVoiceIds = null)
    {
        if (voices.Count == 0)
        {
            return null;
        }

        VoiceCatalogEntry[] languageMatches =
        [
            .. voices.Where(voice => IsLanguageCompatible(voice.LanguageCode, targetLanguageCode))
        ];
        if (languageMatches.Length == 0)
        {
            return null;
        }

        string? normalizedGender = NormalizeGender(gender);
        VoiceCatalogEntry[] genderMatches = normalizedGender is null
            ? languageMatches
            :
            [
                .. languageMatches.Where(voice => NormalizeGender(voice.Gender) == normalizedGender)
            ];
        VoiceCatalogEntry[] pool = genderMatches.Length > 0 ? genderMatches : languageMatches;

        IReadOnlySet<string> reserved = reservedVoiceIds ?? new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        VoiceCatalogEntry[] unused =
        [
            .. pool.Where(voice => !reserved.Contains(voice.VoiceId))
        ];
        VoiceCatalogEntry[] finalPool = unused.Length > 0 ? unused : pool;

        return finalPool
            .OrderByDescending(voice => LanguageExactness(voice.LanguageCode, targetLanguageCode))
            .ThenBy(voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(voice => voice.VoiceId, StringComparer.OrdinalIgnoreCase)
            .First();
    }

    public static bool IsLanguageCompatible(string voiceLanguageCode, string? targetLanguageCode)
    {
        string trimmedVoice = voiceLanguageCode.Trim();
        if (trimmedVoice.Length == 0 ||
            trimmedVoice.Equals("mul", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        string? voiceBase = NormalizeBaseLanguage(trimmedVoice);
        string? targetBase = NormalizeBaseLanguage(targetLanguageCode);
        if (voiceBase is null || targetBase is null)
        {
            return true;
        }

        return string.Equals(voiceBase, targetBase, StringComparison.Ordinal);
    }

    private static int LanguageExactness(string voiceLanguageCode, string? targetLanguageCode)
    {
        if (string.IsNullOrWhiteSpace(targetLanguageCode))
        {
            return 0;
        }

        string voice = voiceLanguageCode.Trim().Replace('_', '-').ToLowerInvariant();
        string target = targetLanguageCode.Trim().Replace('_', '-').ToLowerInvariant();
        if (string.Equals(voice, target, StringComparison.Ordinal))
        {
            return 2;
        }

        return string.Equals(NormalizeBaseLanguage(voice), NormalizeBaseLanguage(target), StringComparison.Ordinal)
            ? 1
            : 0;
    }

    private static string? NormalizeGender(string? gender)
    {
        if (string.IsNullOrWhiteSpace(gender))
        {
            return null;
        }

        string normalized = gender.Trim().ToLowerInvariant();
        return normalized is "male" or "female" ? normalized : null;
    }

    private static string? NormalizeBaseLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        string normalized = languageCode.Trim().Replace('_', '-').ToLowerInvariant();
        int separatorIndex = normalized.IndexOf('-');
        return separatorIndex <= 0 ? normalized : normalized[..separatorIndex];
    }
}
