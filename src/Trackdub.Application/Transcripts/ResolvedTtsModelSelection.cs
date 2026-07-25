using Trackdub.Contracts;

namespace Trackdub.Application.Transcripts;

public sealed record ResolvedTtsModelSelection(
    string? ModelAlias,
    bool UsesQwen3CustomVoice,
    bool UsesQwen3Base,
    bool UsesQwen3Engine)
{
    public static ResolvedTtsModelSelection Resolve(
        RuntimeModelSelections selections,
        bool requiresVoiceClone,
        string preferredTier = "balanced")
    {
        ArgumentNullException.ThrowIfNull(selections);

        if (!string.IsNullOrWhiteSpace(selections.TtsModelAlias))
        {
            string alias = selections.TtsModelAlias.Trim();
            return FromAlias(alias);
        }

        if (selections.TtsModelOverride == TtsModelOverride.Qwen3Tts)
        {
            string alias = requiresVoiceClone
                ? Qwen3TtsDefaults.ResolveBaseAlias(preferredTier)
                : Qwen3TtsDefaults.ResolveCustomVoiceAlias(preferredTier);
            return FromAlias(alias);
        }

        return new ResolvedTtsModelSelection(null, false, false, false);
    }

    public static ResolvedTtsModelSelection FromAlias(string? alias)
    {
        if (string.IsNullOrWhiteSpace(alias))
        {
            return new ResolvedTtsModelSelection(null, false, false, false);
        }

        string normalized = alias.Trim();
        return new ResolvedTtsModelSelection(
            normalized,
            Qwen3TtsDefaults.IsCustomVoiceAlias(normalized),
            Qwen3TtsDefaults.IsBaseAlias(normalized),
            Qwen3TtsDefaults.IsAnyQwen3Alias(normalized));
    }
}
