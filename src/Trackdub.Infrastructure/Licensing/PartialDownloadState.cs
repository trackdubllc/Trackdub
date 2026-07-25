using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Licensing;

/// <summary>
/// Tracks trustworthy committed byte counts for resumable downloads and discards stale partials
/// (legacy sparse pre-allocations, orphaned files without metadata, non-resumable offsets).
/// </summary>
internal static class PartialDownloadState
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false,
    };

    public static string MetaPath(string partialPath) => partialPath + ".meta.json";

    public static async Task<long> PrepareResumeAsync(
        HttpClient httpClient,
        Uri sourceUri,
        string partialPath,
        IApplicationLogger? logger,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(partialPath))
        {
            return 0;
        }

        long fileLength = GetFileLength(partialPath);
        if (fileLength <= 0)
        {
            DeleteArtifacts(partialPath);
            return 0;
        }

        PartialDownloadSnapshot? snapshot = TryReadSnapshot(partialPath);
        if (snapshot is null)
        {
            logger?.LogWarning(
                $"Discarding legacy partial without metadata at '{partialPath}' ({FormatBytes(fileLength)} on disk).");
            DeleteArtifacts(partialPath);
            return 0;
        }

        if (!string.Equals(snapshot.SourceUri, sourceUri.ToString(), StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning(
                $"Discarding partial for a different source at '{partialPath}' (expected '{sourceUri}', found '{snapshot.SourceUri}').");
            DeleteArtifacts(partialPath);
            return 0;
        }

        if (snapshot.CommittedBytes <= 0 || snapshot.CommittedBytes > fileLength)
        {
            logger?.LogWarning(
                $"Discarding inconsistent partial at '{partialPath}' (committed {FormatBytes(snapshot.CommittedBytes)}, file {FormatBytes(fileLength)}).");
            DeleteArtifacts(partialPath);
            return 0;
        }

        if (snapshot.CommittedBytes < fileLength)
        {
            logger?.LogWarning(
                $"Discarding sparse or over-allocated partial at '{partialPath}' (committed {FormatBytes(snapshot.CommittedBytes)}, file {FormatBytes(fileLength)}).");
            DeleteArtifacts(partialPath);
            return 0;
        }

        if (!await CanResumeAsync(httpClient, sourceUri, snapshot.CommittedBytes, cancellationToken).ConfigureAwait(false))
        {
            logger?.LogWarning(
                $"Discarding non-resumable partial at '{partialPath}' ({FormatBytes(snapshot.CommittedBytes)} committed).");
            DeleteArtifacts(partialPath);
            return 0;
        }

        logger?.LogInformation(
            $"Resuming download from {FormatBytes(snapshot.CommittedBytes)} at '{partialPath}'.");
        return snapshot.CommittedBytes;
    }

    public static void PersistPartialProgress(string partialPath, Uri sourceUri)
    {
        long fileLength = GetFileLength(partialPath);
        if (fileLength <= 0)
        {
            return;
        }

        RecordCommittedBytes(partialPath, fileLength, null, sourceUri);
    }

    public static void RecordCommittedBytes(
        string partialPath,
        long committedBytes,
        long? expectedTotalBytes,
        Uri sourceUri)
    {
        if (committedBytes <= 0)
        {
            return;
        }

        var snapshot = new PartialDownloadSnapshot(
            committedBytes,
            expectedTotalBytes,
            sourceUri.ToString(),
            DateTimeOffset.UtcNow);

        string metaPath = MetaPath(partialPath);
        string? directory = Path.GetDirectoryName(metaPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempMetaPath = metaPath + ".tmp";
        string json = JsonSerializer.Serialize(snapshot, JsonOptions);
        File.WriteAllText(tempMetaPath, json);
        File.Move(tempMetaPath, metaPath, overwrite: true);
    }

    public static void DeleteArtifacts(string partialPath)
    {
        DeleteIfExists(partialPath);
        DeleteIfExists(MetaPath(partialPath));
    }

    private static async Task<bool> CanResumeAsync(
        HttpClient httpClient,
        Uri sourceUri,
        long committedBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Range = new RangeHeaderValue(committedBytes, null);
        ModelDownloadHttpClientFactory.ApplyAuthentication(request);

        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return response.StatusCode == HttpStatusCode.PartialContent;
    }

    private static PartialDownloadSnapshot? TryReadSnapshot(string partialPath)
    {
        string metaPath = MetaPath(partialPath);
        if (!File.Exists(metaPath))
        {
            return null;
        }

        try
        {
            string json = File.ReadAllText(metaPath);
            return JsonSerializer.Deserialize<PartialDownloadSnapshot>(json, JsonOptions);
        }
        catch
        {
            return null;
        }
    }

    private static long GetFileLength(string partialPath)
    {
        try
        {
            return new FileInfo(partialPath).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static void DeleteIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static string FormatBytes(long bytes)
    {
        const long kb = 1024;
        const long mb = kb * 1024;
        const long gb = mb * 1024;

        return bytes switch
        {
            >= gb => $"{(double)bytes / gb:F1} GB",
            >= mb => $"{(double)bytes / mb:F1} MB",
            >= kb => $"{(double)bytes / kb:F1} KB",
            _ => $"{bytes} B",
        };
    }

    private sealed record PartialDownloadSnapshot(
        long CommittedBytes,
        long? ExpectedTotalBytes,
        string SourceUri,
        DateTimeOffset UpdatedAtUtc);
}
