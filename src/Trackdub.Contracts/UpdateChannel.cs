namespace Trackdub.Contracts;

public enum UpdateChannel
{
    Stable = 0,
    Preview = 1
}

public static class UpdateChannelSettings
{
    public const string StableKey = "stable";
    public const string PreviewKey = "preview";

    public static string ToKey(UpdateChannel channel) =>
        channel switch
        {
            UpdateChannel.Preview => PreviewKey,
            _ => StableKey
        };

    public static UpdateChannel FromKey(string? key) =>
        string.IsNullOrWhiteSpace(key)
            ? UpdateChannel.Stable
            : key.Trim().ToLowerInvariant() switch
            {
                PreviewKey => UpdateChannel.Preview,
                _ => UpdateChannel.Stable
            };
}

public sealed record UpdateCheckResult(
    string AvailableVersion,
    string? ReleaseNotesUrl,
    string? DownloadUrl,
    UpdateChannel Channel,
    bool IsUpdateAvailable);
