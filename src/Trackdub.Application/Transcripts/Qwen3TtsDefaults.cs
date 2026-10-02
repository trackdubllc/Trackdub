namespace Trackdub.Application.Transcripts;

public static class Qwen3TtsDefaults
{
    public const string CustomVoice06Alias = "qwen3-tts-0.6b-customvoice";
    public const string CustomVoice17Alias = "qwen3-tts-1.7b-customvoice";
    public const string Base06Alias = "qwen3-tts-0.6b-base";
    public const string Base17Alias = "qwen3-tts-1.7b-base";
    public const string LegacyAlias = "qwen3-tts";

    /// <summary>Voice ID prefix for Qwen3 CustomVoice presets, e.g. <c>qwen3:vivian</c>.</summary>
    public const string PresetVoicePrefix = "qwen3:";

    /// <summary>True when <paramref name="voiceId"/> names a Qwen3 CustomVoice preset.</summary>
    public static bool IsPresetVoiceId(string? voiceId) =>
        !string.IsNullOrWhiteSpace(voiceId) &&
        voiceId.Trim().StartsWith(PresetVoicePrefix, StringComparison.OrdinalIgnoreCase) &&
        voiceId.Trim().Length > PresetVoicePrefix.Length;

    /// <summary>
    /// Default CustomVoice preset for a target language: a preset whose native language matches
    /// (Mandarin, Japanese, Korean), otherwise the English preset Ryan. Native languages come from
    /// the upstream model card; every preset can speak all ten supported languages.
    /// </summary>
    public static string ResolveDefaultPresetVoiceId(string? targetLanguage)
    {
        string normalized = string.IsNullOrWhiteSpace(targetLanguage)
            ? string.Empty
            : targetLanguage.Trim().Split('-')[0].ToLowerInvariant();
        return normalized switch
        {
            "zh" => PresetVoicePrefix + "vivian",
            "ja" => PresetVoicePrefix + "ono_anna",
            "ko" => PresetVoicePrefix + "sohee",
            _ => PresetVoicePrefix + "ryan",
        };
    }

    public static string ResolveCustomVoiceAlias(string? tier) =>
        IsQualityTier(tier) ? CustomVoice17Alias : CustomVoice06Alias;

    public static string ResolveBaseAlias(string? tier) =>
        IsQualityTier(tier) ? Base17Alias : Base06Alias;

    public static bool IsCustomVoiceAlias(string? alias) =>
        !string.IsNullOrWhiteSpace(alias) &&
        (alias.Equals(CustomVoice06Alias, StringComparison.OrdinalIgnoreCase) ||
         alias.Equals(CustomVoice17Alias, StringComparison.OrdinalIgnoreCase) ||
         alias.Equals(LegacyAlias, StringComparison.OrdinalIgnoreCase) ||
         alias.Equals("qwen-tts", StringComparison.OrdinalIgnoreCase) ||
         alias.Equals("qwen3-tts-0.6b", StringComparison.OrdinalIgnoreCase) ||
         alias.Equals("qwen3-tts-1.7b", StringComparison.OrdinalIgnoreCase));

    public static bool IsBaseAlias(string? alias) =>
        !string.IsNullOrWhiteSpace(alias) &&
        (alias.Equals(Base06Alias, StringComparison.OrdinalIgnoreCase) ||
         alias.Equals(Base17Alias, StringComparison.OrdinalIgnoreCase));

    public static bool IsAnyQwen3Alias(string? alias) =>
        IsCustomVoiceAlias(alias) || IsBaseAlias(alias);

    public static bool IsLargeAlias(string? alias) =>
        !string.IsNullOrWhiteSpace(alias) &&
        alias.Contains("1.7b", StringComparison.OrdinalIgnoreCase);

    private static bool IsQualityTier(string? tier) =>
        string.Equals(tier, "quality", StringComparison.OrdinalIgnoreCase);
}
