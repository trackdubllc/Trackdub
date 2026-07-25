namespace Trackdub.Application.Transcripts;

public static class Qwen3TtsDefaults
{
    public const string CustomVoice06Alias = "qwen3-tts-0.6b-customvoice";
    public const string CustomVoice17Alias = "qwen3-tts-1.7b-customvoice";
    public const string Base06Alias = "qwen3-tts-0.6b-base";
    public const string Base17Alias = "qwen3-tts-1.7b-base";
    public const string LegacyAlias = "qwen3-tts";

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
