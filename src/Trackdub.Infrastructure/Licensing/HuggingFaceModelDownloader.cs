using System.Security.Cryptography;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Logging;
using Trackdub.Infrastructure.Retry;

namespace Trackdub.Infrastructure.Licensing;

/// <summary>
/// Downloads model files from Hugging Face Hub.
/// </summary>
public sealed class HuggingFaceModelDownloader : IModelDownloader
{
    private readonly string modelCacheRoot;
    private readonly IApplicationLogger logger;
    private readonly HttpClient httpClient;
    private readonly HuggingFaceDownloadOptions downloadOptions;
    private readonly HuggingFaceCliDownloader cliDownloader;

    private const string HuggingFaceApiBase = "https://huggingface.co";
    private const int BufferSize = 65536; // 64 KB chunks

    public HuggingFaceModelDownloader(
        string modelCacheRoot,
        IApplicationLogger? logger = null,
        HttpClient? httpClient = null,
        HuggingFaceDownloadOptions? downloadOptions = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelCacheRoot);
        this.modelCacheRoot = Path.GetFullPath(modelCacheRoot);
        this.logger = logger ?? new DebugApplicationLogger();
        this.httpClient = httpClient ?? new HttpClient();
        this.downloadOptions = downloadOptions ?? HuggingFaceDownloadOptions.Default;
        cliDownloader = new HuggingFaceCliDownloader(this.downloadOptions, this.logger);
    }

    public async Task<bool> DownloadAsync(
        string modelId,
        string fileName,
        string destinationPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? revision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        string resolvedDestinationPath = ResolveDestinationPath(destinationPath);
        // Deterministic temp name (matches ModelDownloaderAdapter) so an interrupted download
        // resumes across separate calls/app restarts instead of restarting from byte 0 and
        // orphaning a fresh GUID-named partial on every attempt.
        string tempPath = $"{resolvedDestinationPath}.partial";

        string resolvedRevision = string.IsNullOrWhiteSpace(revision) ? "main" : revision.Trim();
        Uri downloadUri = BuildDownloadUri(modelId, resolvedRevision, fileName);
        logger.LogInformation($"Downloading model from {downloadUri}");

        // Ensure destination directory exists
        string? directory = Path.GetDirectoryName(resolvedDestinationPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        if (downloadOptions.CliPreference == HuggingFaceCliPreference.Required)
        {
            // Required CLI mode must not probe HTTP resume state (HEAD/Range).
            // Clear any leftover HTTP partial, attempt CLI, then refuse HTTP fallback.
            try
            {
                if (File.Exists(tempPath))
                {
                    File.Delete(tempPath);
                }
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(
                    $"Failed to clear partial download before required Hugging Face CLI attempt: {tempPath}",
                    ex);
            }

            if (await cliDownloader.TryDownloadAsync(
                    modelId,
                    fileName,
                    resolvedDestinationPath,
                    resolvedRevision,
                    progress,
                    cancellationToken).ConfigureAwait(false))
            {
                CleanupStaleTemps(resolvedDestinationPath);
                return true;
            }

            logger.LogError(
                $"TRACKDUB_HF_USE_CLI=require: Hugging Face CLI did not download '{modelId}/{fileName}'; refusing HTTP fallback. " +
                "To enable HTTP fallback instead of requiring the CLI, set TRACKDUB_HF_USE_CLI=auto or TRACKDUB_HF_USE_CLI=never.");
            return false;
        }

        long existingBytes = await PartialDownloadState.PrepareResumeAsync(
            httpClient,
            downloadUri,
            tempPath,
            logger,
            cancellationToken).ConfigureAwait(false);

        if (existingBytes == 0 &&
            await cliDownloader.TryDownloadAsync(
                modelId,
                fileName,
                resolvedDestinationPath,
                resolvedRevision,
                progress,
                cancellationToken).ConfigureAwait(false))
        {
            CleanupStaleTemps(resolvedDestinationPath);
            return true;
        }

        if (existingBytes == 0 &&
            await ParallelRangeDownloader.TryDownloadAsync(
                httpClient,
                downloadUri,
                tempPath,
                downloadOptions,
                logger,
                progress,
                cancellationToken).ConfigureAwait(false))
        {
            File.Move(tempPath, resolvedDestinationPath, overwrite: true);
            CleanupStaleTemps(resolvedDestinationPath);
            logger.LogInformation($"Parallel download completed: {resolvedDestinationPath}");
            return true;
        }

        string url = downloadUri.ToString();
        for (int attempt = 1; attempt <= RetryPolicy.Download.MaxAttempts; attempt++)
        {
            try
            {
                if (attempt > 1)
                {
                    existingBytes = await PartialDownloadState.PrepareResumeAsync(
                        httpClient,
                        downloadUri,
                        tempPath,
                        logger,
                        cancellationToken).ConfigureAwait(false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                if (existingBytes > 0)
                {
                    request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(existingBytes, null);
                }

                using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

                bool isResuming = existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.PartialContent;
                if (existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.OK)
                {
                    logger.LogWarning($"Hugging Face ignored resume request; restarting '{url}'.");
                    existingBytes = 0;
                    isResuming = false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (existingBytes > 0 && response.StatusCode == System.Net.HttpStatusCode.RequestedRangeNotSatisfiable && attempt < RetryPolicy.Download.MaxAttempts)
                    {
                        logger.LogWarning($"Hugging Face partial download was no longer resumable; restarting '{url}'.");
                        PartialDownloadState.DeleteArtifacts(tempPath);
                        existingBytes = 0;
                        await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    logger.LogError($"Download failed with status {response.StatusCode}: {url}");

                    if (DownloadRetry.ShouldRetryStatus(response.StatusCode) && attempt < RetryPolicy.Download.MaxAttempts)
                    {
                        await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    PartialDownloadState.DeleteArtifacts(tempPath);
                    return false;
                }

                long? contentLength = ResolveTotalContentLength(response, existingBytes, isResuming);
                long totalBytesRead = isResuming ? existingBytes : 0;
                var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                var lastReportTime = stopwatch.Elapsed;
                long sessionBytesRead = 0;

                await using (Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
                await using (var fileStream = new FileStream(tempPath, isResuming ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true))
                {
                    byte[] buffer = new byte[BufferSize];
                    int bytesRead;

                    while ((bytesRead = await contentStream.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false)) != 0)
                    {
                        await fileStream.WriteAsync(buffer, 0, bytesRead, cancellationToken).ConfigureAwait(false);
                        totalBytesRead += bytesRead;
                        sessionBytesRead += bytesRead;

                        var now = stopwatch.Elapsed;
                        if (now - lastReportTime >= TimeSpan.FromMilliseconds(250) || totalBytesRead == contentLength)
                        {
                            int percentComplete = contentLength is > 0 and var totalBytes
                                ? (int)((totalBytesRead * 100) / totalBytes)
                                : 0;
                            double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                            double speed = elapsedSeconds > 0 ? sessionBytesRead / elapsedSeconds : 0;
                            TimeSpan? eta = null;
                            if (contentLength is > 0 and var totalBytesForEta && speed > 0)
                            {
                                long remainingBytes = totalBytesForEta - totalBytesRead;
                                eta = TimeSpan.FromSeconds(remainingBytes / speed);
                            }

                            progress?.Report(new DownloadProgress(
                                totalBytesRead,
                                contentLength,
                                percentComplete,
                                $"Downloaded {FormatBytes(totalBytesRead)} of {(contentLength is { } total ? FormatBytes(total) : "unknown")}",
                                speed,
                                eta));

                            PartialDownloadState.RecordCommittedBytes(
                                tempPath,
                                totalBytesRead,
                                contentLength,
                                downloadUri);

                            lastReportTime = now;
                        }
                    }
                }

                PartialDownloadState.RecordCommittedBytes(
                    tempPath,
                    totalBytesRead,
                    contentLength,
                    downloadUri);

                File.Move(tempPath, resolvedDestinationPath, overwrite: true);
                CleanupStaleTemps(resolvedDestinationPath);
                logger.LogInformation($"Download completed: {resolvedDestinationPath} ({FormatBytes(totalBytesRead)})");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                logger.LogInformation(
                    $"Download cancelled; partial file retained at '{tempPath}' for resume.");
                throw;
            }
            catch (Exception ex) when (DownloadRetry.IsTransientException(ex, cancellationToken) && attempt < RetryPolicy.Download.MaxAttempts)
            {
                logger.LogWarning($"Download interrupted for '{url}'. Retrying attempt {attempt + 1} of {RetryPolicy.Download.MaxAttempts}.", ex);
                await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError($"Download failed: {resolvedDestinationPath}", ex);
                PartialDownloadState.DeleteArtifacts(tempPath);
                return false;
            }
        }

        logger.LogError($"Download failed after {RetryPolicy.Download.MaxAttempts} attempts: {url}");
        return false;
    }

    private static long? ResolveTotalContentLength(HttpResponseMessage response, long existingBytes, bool isResuming)
    {
        if (isResuming && response.Content.Headers.ContentRange?.Length is long totalLength)
        {
            return totalLength;
        }

        return response.Content.Headers.ContentLength is long contentLength
            ? existingBytes + contentLength
            : null;
    }

    public async Task<bool> VerifyHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(filePath))
        {
            logger.LogError($"File not found for hash verification: {filePath}");
            return false;
        }

        try
        {
            await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            string actualHash = Convert.ToHexString(hash).ToLowerInvariant();

            if (string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation($"Hash verification passed: {filePath}");
                return true;
            }

            logger.LogError($"Hash mismatch for {filePath}: expected {expectedHash}, got {actualHash}");
            return false;
        }
        catch (Exception ex)
        {
            logger.LogError($"Hash verification failed: {filePath}", ex);
            return false;
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
            _ => $"{bytes} B"
        };
    }

    private Uri BuildDownloadUri(string modelId, string revision, string fileName)
    {
        string encodedRevision = Uri.EscapeDataString(revision);
        string path = $"{HuggingFaceApiBase}/{modelId}/resolve/{encodedRevision}/{fileName}";
        if (downloadOptions.DisableXet)
        {
            path += "?download=true";
        }

        return new Uri(path);
    }

    private string ResolveDestinationPath(string destinationPath)
    {
        string resolvedDestinationPath = Path.GetFullPath(destinationPath);
        string cacheRootWithSeparator = modelCacheRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) +
                                        Path.DirectorySeparatorChar;
        if (!resolvedDestinationPath.StartsWith(cacheRootWithSeparator, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Model download destination '{destinationPath}' must be inside the configured model cache root '{modelCacheRoot}'.");
        }

        return resolvedDestinationPath;
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


    // Best-effort sweep of orphaned partial downloads for a just-completed target file:
    // the deterministic ".partial" and any legacy random-GUID ".{guid}.tmp" siblings left by
    // interrupted/duplicate downloads of the same file.
    private static void CleanupStaleTemps(string resolvedDestinationPath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(resolvedDestinationPath);
            if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
            {
                return;
            }

            string fileName = Path.GetFileName(resolvedDestinationPath);
            DeleteIfExists($"{resolvedDestinationPath}.partial");
            DeleteIfExists(PartialDownloadState.MetaPath($"{resolvedDestinationPath}.partial"));
            foreach (string stale in Directory.EnumerateFiles(directory, $"{fileName}.*.tmp"))
            {
                DeleteIfExists(stale);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }
}
