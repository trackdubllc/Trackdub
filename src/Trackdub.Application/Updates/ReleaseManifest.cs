namespace Trackdub.Application.Updates;

public sealed record ReleaseEntry(
    string Version,
    Uri DownloadUrl,
    string Sha256,
    string? ReleaseNotesUrl,
    DateTimeOffset PublishedAt);

public sealed record UpdateCheckResult(
    bool UpdateAvailable,
    ReleaseEntry? Release,
    string? ErrorMessage);

public sealed record UpdateDownloadResult(
    bool Success,
    string? FilePath,
    string? ErrorMessage);
