using System.Text.Json.Serialization;

namespace Trackdub.Infrastructure.Updates;

internal sealed record ReleaseManifestSchema(
    [property: JsonPropertyName("latestVersion")] string LatestVersion,
    [property: JsonPropertyName("downloadUrl")] string DownloadUrl,
    [property: JsonPropertyName("sha256")] string Sha256,
    [property: JsonPropertyName("releaseNotesUrl")] string? ReleaseNotesUrl,
    [property: JsonPropertyName("publishedAt")] DateTimeOffset PublishedAt,
    [property: JsonPropertyName("isPrerelease")] bool IsPrerelease);
