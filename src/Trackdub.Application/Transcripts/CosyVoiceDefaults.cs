namespace Trackdub.Application.Transcripts;

public static class CosyVoiceDefaults
{
    public const string PrimaryAlias = VoiceCloningDefaults.CosyVoicePrimaryAlias;
    public const string FallbackAlias = VoiceCloningDefaults.CosyVoiceFallbackAlias;

    public static bool IsCosyVoiceAlias(string? alias) =>
        string.Equals(alias, PrimaryAlias, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(alias, FallbackAlias, StringComparison.OrdinalIgnoreCase);
}
