using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Application.Services;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Updates;

public sealed class ReleaseManifestUpdateService(
    HttpClient httpClient,
    IApplicationLogger? logger = null) : IUpdateService
{
    private const string DefaultManifestUrl = "https://api.trackdub.com/releases/manifest.json";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public async Task<UpdateCheckResult> CheckForUpdatesAsync(
        UpdateChannel channel,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentVersion);

        try
        {
            string manifestUrl = await BuildManifestUrlAsync(channel, cancellationToken).ConfigureAwait(false);
            string json = await httpClient.GetStringAsync(manifestUrl, cancellationToken).ConfigureAwait(false);

            ReleaseManifest? manifest = JsonSerializer.Deserialize<ReleaseManifest>(json, JsonOptions);
            if (manifest is null || manifest.LatestVersion is null)
            {
                return NoUpdate(channel, currentVersion);
            }

            bool isNewer = CompareVersions(manifest.LatestVersion, currentVersion) > 0;
            return new UpdateCheckResult(
                AvailableVersion: manifest.LatestVersion,
                ReleaseNotesUrl: manifest.ReleaseNotesUrl,
                DownloadUrl: manifest.DownloadUrl,
                Channel: channel,
                IsUpdateAvailable: isNewer);
        }
        catch (HttpRequestException ex)
        {
            logger?.LogWarning($"Update check failed (network): {ex.Message}");
            return NoUpdate(channel, currentVersion);
        }
        catch (TaskCanceledException)
        {
            logger?.LogWarning("Update check timed out.");
            return NoUpdate(channel, currentVersion);
        }
        catch (JsonException ex)
        {
            logger?.LogWarning($"Update check failed (bad manifest): {ex.Message}");
            return NoUpdate(channel, currentVersion);
        }
    }

    private Task<string> BuildManifestUrlAsync(
        UpdateChannel channel,
        CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        return Task.FromResult(
            channel == UpdateChannel.Preview
                ? DefaultManifestUrl.Replace(".json", "-preview.json", StringComparison.OrdinalIgnoreCase)
                : DefaultManifestUrl);
    }

    private static UpdateCheckResult NoUpdate(UpdateChannel channel, string currentVersion) =>
        new(currentVersion, null, null, channel, IsUpdateAvailable: false);

    private static int CompareVersions(string versionA, string versionB)
    {
        if (Version.TryParse(SanitizeVersion(versionA), out Version? va) &&
            Version.TryParse(SanitizeVersion(versionB), out Version? vb))
        {
            return va.CompareTo(vb);
        }

        return string.Compare(versionA, versionB, StringComparison.OrdinalIgnoreCase);
    }

    private static string SanitizeVersion(string version)
    {
        int idx = version.IndexOfAny(['-', '+']);
        return idx >= 0 ? version[..idx] : version;
    }

    private sealed record ReleaseManifest(
        string? LatestVersion,
        string? ReleaseNotesUrl,
        string? DownloadUrl,
        string? ReleaseDate);
}
