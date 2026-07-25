using System.Net;
using System.Net.Http.Headers;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Logging;
using Trackdub.Infrastructure.Retry;

namespace Trackdub.Infrastructure.Licensing;

/// <summary>
/// Adapter that bridges Infrastructure `IModelDownloader` to Application `IModelDownloaderContract`.
/// </summary>
public sealed class ModelDownloaderAdapter(
    IModelDownloader innerDownloader,
    HttpClient? httpClient = null,
    IApplicationLogger? logger = null,
    HuggingFaceDownloadOptions? downloadOptions = null)
    : IModelDownloaderContract, IDisposable
{
    private const int BufferSize = 65536;

    private readonly IModelDownloader innerDownloader = innerDownloader ?? throw new ArgumentNullException(nameof(innerDownloader));
    private readonly HttpClient httpClient = httpClient ?? new HttpClient();
    private readonly bool ownsHttpClient = httpClient is null;
    private readonly IApplicationLogger logger = logger ?? new DebugApplicationLogger();
    private readonly HuggingFaceDownloadOptions downloadOptions = downloadOptions ?? HuggingFaceDownloadOptions.Default;
    private bool disposed;

    public async Task<bool> DownloadAsync(
        string modelId,
        string fileName,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default,
        string? revision = null)
    {
        ThrowIfDisposed();

        // Adapt from Application progress to Infrastructure progress
        IProgress<DownloadProgress>? infraProgress = progress is null
            ? null
            : new Progress<DownloadProgress>(infra =>
            {
                progress.Report(new ModelDownloadProgress(
                    infra.BytesDownloaded,
                    infra.TotalBytes,
                    infra.PercentComplete,
                    infra.Message,
                    infra.DownloadSpeedBytesPerSecond,
                    infra.EstimatedTimeRemaining));
            });

        return await innerDownloader.DownloadAsync(
            modelId,
            fileName,
            destinationPath,
            infraProgress,
            cancellationToken,
            revision).ConfigureAwait(false);
    }

    public async Task<bool> DownloadUriAsync(
        Uri sourceUri,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        ArgumentNullException.ThrowIfNull(sourceUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);
        if (!sourceUri.IsAbsoluteUri ||
            sourceUri.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("Runtime support file downloads require an absolute HTTP(S) URI.", nameof(sourceUri));
        }

        string resolvedDestinationPath = Path.GetFullPath(destinationPath);
        string tempPath = $"{resolvedDestinationPath}.partial";
        Uri effectiveUri = MaybePreferClassicCdn(sourceUri);

        long existingBytes = await PartialDownloadState.PrepareResumeAsync(
            httpClient,
            effectiveUri,
            tempPath,
            logger,
            cancellationToken).ConfigureAwait(false);

        if (IsHuggingFaceHost(effectiveUri) &&
            existingBytes == 0)
        {
            try
            {
                if (await ParallelRangeDownloader.TryDownloadAsync(
                        httpClient,
                        effectiveUri,
                        tempPath,
                        downloadOptions,
                        logger,
                        AdaptProgress(progress),
                        cancellationToken).ConfigureAwait(false))
                {
                    File.Move(tempPath, resolvedDestinationPath, overwrite: true);
                    DeleteIfExists(PartialDownloadState.MetaPath(tempPath));
                    logger.LogInformation($"Runtime support file parallel download completed: {resolvedDestinationPath}");
                    return true;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning($"Parallel runtime support download probe failed for '{effectiveUri}'. Falling back to single-stream download.", ex);
                PartialDownloadState.DeleteArtifacts(tempPath);
            }
        }

        for (int attempt = 1; attempt <= RetryPolicy.Download.MaxAttempts; attempt++)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(resolvedDestinationPath)!);
                if (attempt > 1)
                {
                    existingBytes = await PartialDownloadState.PrepareResumeAsync(
                        httpClient,
                        effectiveUri,
                        tempPath,
                        logger,
                        cancellationToken).ConfigureAwait(false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, effectiveUri);
                if (existingBytes > 0)
                {
                    request.Headers.Range = new RangeHeaderValue(existingBytes, null);
                }

                using HttpResponseMessage response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);
                bool isResuming = existingBytes > 0 && response.StatusCode == HttpStatusCode.PartialContent;
                if (existingBytes > 0 && response.StatusCode == HttpStatusCode.OK)
                {
                    logger.LogWarning(
                        $"Runtime support file download source ignored resume request; restarting '{sourceUri}'.");
                    existingBytes = 0;
                    isResuming = false;
                }

                if (!response.IsSuccessStatusCode)
                {
                    if (existingBytes > 0 &&
                        response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable &&
                        attempt < RetryPolicy.Download.MaxAttempts)
                    {
                        logger.LogWarning(
                            $"Runtime support file partial download was no longer resumable; restarting '{sourceUri}'.");
                        PartialDownloadState.DeleteArtifacts(tempPath);
                        existingBytes = 0;
                        await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    logger.LogError($"Runtime support file download failed with status {response.StatusCode}: {sourceUri}");

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

                await using (Stream contentStream = await response.Content
                                 .ReadAsStreamAsync(cancellationToken)
                                 .ConfigureAwait(false))
                await using (var fileStream = new FileStream(
                                 tempPath,
                                 isResuming ? FileMode.Append : FileMode.Create,
                                 FileAccess.Write,
                                 FileShare.None,
                                 BufferSize,
                                 useAsync: true))
                {
                    byte[] buffer = new byte[BufferSize];
                    int bytesRead;
                    while ((bytesRead = await contentStream
                               .ReadAsync(buffer, 0, buffer.Length, cancellationToken)
                               .ConfigureAwait(false)) != 0)
                    {
                        await fileStream
                            .WriteAsync(buffer, 0, bytesRead, cancellationToken)
                            .ConfigureAwait(false);
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

                            progress?.Report(new ModelDownloadProgress(
                                totalBytesRead,
                                contentLength,
                                percentComplete,
                                $"Downloaded {totalBytesRead} bytes from {sourceUri.Host}",
                                speed,
                                eta));

                            PartialDownloadState.RecordCommittedBytes(
                                tempPath,
                                totalBytesRead,
                                contentLength,
                                effectiveUri);

                            lastReportTime = now;
                        }
                    }
                }

                PartialDownloadState.RecordCommittedBytes(
                    tempPath,
                    totalBytesRead,
                    contentLength,
                    effectiveUri);

                File.Move(tempPath, resolvedDestinationPath, overwrite: true);
                DeleteIfExists(PartialDownloadState.MetaPath(tempPath));
                logger.LogInformation($"Runtime support file download completed: {resolvedDestinationPath}");
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    PartialDownloadState.PersistPartialProgress(tempPath, effectiveUri);
                }
                catch (Exception persistEx)
                {
                    logger.LogWarning(
                        $"Runtime support file download could not persist partial progress for '{sourceUri}'.",
                        persistEx);
                }

                throw;
            }
            catch (Exception ex) when (DownloadRetry.IsTransientException(ex, cancellationToken) && attempt < RetryPolicy.Download.MaxAttempts)
            {
                try
                {
                    PartialDownloadState.PersistPartialProgress(tempPath, effectiveUri);
                }
                catch (Exception persistEx)
                {
                    logger.LogWarning(
                        $"Runtime support file download could not persist partial progress for '{sourceUri}'.",
                        persistEx);
                }

                logger.LogWarning(
                    $"Runtime support file download interrupted for '{sourceUri}'. Retrying attempt {attempt + 1} of {RetryPolicy.Download.MaxAttempts}.",
                    ex);
                await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                logger.LogError($"Runtime support file download failed: {sourceUri}", ex);
                PartialDownloadState.DeleteArtifacts(tempPath);
                return false;
            }
        }

        logger.LogError($"Runtime support file download failed after {RetryPolicy.Download.MaxAttempts} attempts: {sourceUri}");
        return false;
    }

    public Task<bool> VerifyHashAsync(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        return innerDownloader.VerifyHashAsync(filePath, expectedHash, cancellationToken);
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (ownsHttpClient)
        {
            httpClient.Dispose();
        }

        disposed = true;
    }

    private static IProgress<DownloadProgress>? AdaptProgress(IProgress<ModelDownloadProgress>? progress) =>
        progress is null
            ? null
            : new Progress<DownloadProgress>(infra =>
                progress.Report(new ModelDownloadProgress(
                    infra.BytesDownloaded,
                    infra.TotalBytes,
                    infra.PercentComplete,
                    infra.Message,
                    infra.DownloadSpeedBytesPerSecond,
                    infra.EstimatedTimeRemaining)));

    private static bool IsHuggingFaceHost(Uri sourceUri) =>
        sourceUri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase);

    private Uri MaybePreferClassicCdn(Uri sourceUri)
    {
        if (!downloadOptions.DisableXet ||
            !sourceUri.Host.Equals("huggingface.co", StringComparison.OrdinalIgnoreCase))
        {
            return sourceUri;
        }

        string existingQuery = sourceUri.Query.TrimStart('?');
        string newQuery = string.IsNullOrEmpty(existingQuery)
            ? "download=true"
            : $"{existingQuery}&download=true";
        return new UriBuilder(sourceUri) { Query = newQuery }.Uri;
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
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
            // Preserve the original download failure.
        }
    }

    private static long? ResolveTotalContentLength(
        HttpResponseMessage response,
        long existingBytes,
        bool isResuming)
    {
        if (isResuming && response.Content.Headers.ContentRange?.Length is long totalLength)
        {
            return totalLength;
        }

        return response.Content.Headers.ContentLength is long contentLength
            ? existingBytes + contentLength
            : null;
    }
}

