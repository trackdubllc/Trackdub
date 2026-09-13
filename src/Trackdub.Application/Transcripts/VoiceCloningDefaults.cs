namespace Trackdub.Application.Transcripts;

public static class VoiceCloningDefaults
{
    /// <summary>English-only, latency-optimized clone model. Default for English targets.</summary>
    public const string ChatterboxPrimaryAlias = "chatterbox-turbo-onnx";
    public const string ChatterboxFallbackAlias = "chatterbox-onnx";

    /// <summary>
    /// 22-language clone model. Default for non-English targets — the English-only
    /// turbo/base models would otherwise synthesize English-sounding audio for a
    /// non-English target, which is a fake-readiness failure.
    /// </summary>
    public const string ChatterboxMultilingualAlias = "chatterbox-multilingual";

    public const string CosyVoicePrimaryAlias = "cosyvoice-300m";
    public const string CosyVoiceFallbackAlias = "cosyvoice";

    /// <summary>
    /// True when <paramref name="alias"/> names a voice-cloning model (Chatterbox
    /// primary/fallback/multilingual or CosyVoice primary/fallback). These models require a
    /// reference clip and are never valid stock voicepack ids, so a non-clone run must not
    /// attempt a stock voicepack lookup against them.
    /// </summary>
    public static bool IsVoiceCloningModelAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        string normalized = alias.Trim();
        return normalized.Equals(ChatterboxPrimaryAlias, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(ChatterboxFallbackAlias, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(ChatterboxMultilingualAlias, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(CosyVoicePrimaryAlias, StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals(CosyVoiceFallbackAlias, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Picks the default clone-model alias for a target language: the English-only turbo
    /// model for English, the multilingual model otherwise.
    /// </summary>
    public static string ResolveDefaultChatterboxAlias(string? targetLanguage)
    {
        string normalized = string.IsNullOrWhiteSpace(targetLanguage)
            ? "en"
            : targetLanguage.Trim().Split('-')[0].ToLowerInvariant();

        return normalized == "en"
            ? ChatterboxPrimaryAlias
            : ChatterboxMultilingualAlias;
    }
}
