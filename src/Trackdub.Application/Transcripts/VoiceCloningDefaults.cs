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
    /// True when <paramref name="alias"/> names a Chatterbox or CosyVoice voice-cloning model
    /// (Chatterbox primary/fallback/multilingual or CosyVoice primary/fallback). These models
    /// require a reference clip and are never valid stock voicepack ids.
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
    /// True when <paramref name="alias"/> names an F5-TTS voice-cloning model. Like the other
    /// clone models it requires a reference clip and is never a stock voicepack id.
    /// </summary>
    public static bool IsF5VoiceCloningModelAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return false;
        }

        string normalized = alias.Trim();
        return normalized.Equals("f5", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("f5tts", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("f5tts-onnx", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("f5-tts", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("f5-tts-onnx", StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("swivid-f5-tts", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when <paramref name="alias"/> names ANY clone-only model that requires a reference
    /// clip and is never a valid stock voicepack id: Chatterbox (primary/fallback/multilingual),
    /// CosyVoice (primary/fallback), Qwen3-base, or F5. This is the single source of truth for
    /// "clone-only alias" shared by <c>StartTtsStageHandler</c>'s clone recognition and the
    /// non-clone-run substitution guard, so the two cannot drift and leave a clone alias to fall
    /// through to a stock voicepack lookup that would throw "Voicepack '...' is not available.".
    /// </summary>
    public static bool IsCloneOnlyModelAlias(string? alias) =>
        IsVoiceCloningModelAlias(alias) ||
        Qwen3TtsDefaults.IsBaseAlias(alias?.Trim()) ||
        IsF5VoiceCloningModelAlias(alias);

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
