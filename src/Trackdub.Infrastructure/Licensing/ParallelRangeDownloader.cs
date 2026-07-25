using System.Net;
using System.Net.Http.Headers;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Retry;

namespace Trackdub.Infrastructure.Licensing;

internal static class ParallelRangeDownloader
{
    private static readonly TimeSpan ReadStallTimeout = TimeSpan.FromSeconds(120);

    public static async Task<bool> TryDownloadAsync(
        HttpClient httpClient,
        Uri sourceUri,
        string tempPath,
        HuggingFaceDownloadOptions options,
        IApplicationLogger logger,
        IProgress<DownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (!options.ParallelDownloadsEnabled)
        {
            return false;
        }

        long existingBytes = GetPartialDownloadLength(tempPath);
        if (existingBytes > 0)
        {
            // Parallel downloads are fresh-start only; resumable partials use the sequential path.
            return false;
        }

        DownloadProbe? probe;
        try
        {
            if (await TryProbeAsync(httpClient, sourceUri, cancellationToken).ConfigureAwait(false)
                is not { } resolvedProbe)
            {
                return false;
            }

            probe = resolvedProbe;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Parallel download probe failed for '{sourceUri}'.", ex);
            return false;
        }

        if (probe.TotalBytes < options.MinFileSizeForParallelBytes)
        {
            return false;
        }

        IReadOnlyList<(long Start, long End)> ranges = BuildRanges(
            probe.TotalBytes,
            options.ChunkSizeBytes).ToList();
        if (ranges.Count == 0)
        {
            return false;
        }

        int maxParallelism = Math.Min(options.MaxParallelConnections, ranges.Count);

        logger.LogInformation(
            $"Using parallel Hugging Face download ({maxParallelism} concurrent connections, {ranges.Count} chunks, {FormatBytes(probe.TotalBytes)}): {sourceUri}");

        Directory.CreateDirectory(Path.GetDirectoryName(tempPath)!);

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        long completedBytes = 0;
        object progressLock = new();
        var lastReportTime = stopwatch.Elapsed;

        void ReportProgress(long reportedBytes)
        {
            lock (progressLock)
            {
                var now = stopwatch.Elapsed;
                if (now - lastReportTime < TimeSpan.FromMilliseconds(250) && reportedBytes < probe.TotalBytes)
                {
                    return;
                }

                lastReportTime = now;
                double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                double speed = elapsedSeconds > 0 ? reportedBytes / elapsedSeconds : 0;
                TimeSpan? eta = speed > 0
                    ? TimeSpan.FromSeconds((probe.TotalBytes - reportedBytes) / speed)
                    : null;
                int percentComplete = (int)((reportedBytes * 100) / probe.TotalBytes);
                progress?.Report(new DownloadProgress(
                    reportedBytes,
                    probe.TotalBytes,
                    percentComplete,
                    $"Downloaded {FormatBytes(reportedBytes)} of {FormatBytes(probe.TotalBytes)} (parallel)",
                    speed,
                    eta));
            }
        }

        try
        {
            await Parallel.ForEachAsync(
                ranges,
                new ParallelOptions
                {
                    MaxDegreeOfParallelism = maxParallelism,
                    CancellationToken = cancellationToken,
                },
                async (range, token) =>
                {
                    await DownloadRangeAsync(
                        httpClient,
                        sourceUri,
                        tempPath,
                        range.Start,
                        range.End,
                        bytesRead =>
                        {
                            long reportedBytes = Interlocked.Add(ref completedBytes, bytesRead);
                            ReportProgress(reportedBytes);
                        },
                        token).ConfigureAwait(false);
                }).ConfigureAwait(false);

            long actualLength = new FileInfo(tempPath).Length;
            if (actualLength != probe.TotalBytes)
            {
                throw new IOException(
                    $"Parallel download size mismatch for '{sourceUri}': expected {probe.TotalBytes} bytes, got {actualLength}.");
            }
        }
        catch (OperationCanceledException)
        {
            PartialDownloadState.DeleteArtifacts(tempPath);
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"Parallel download failed for '{sourceUri}'. Falling back to single-stream download.", ex);
            PartialDownloadState.DeleteArtifacts(tempPath);
            return false;
        }

        PartialDownloadState.RecordCommittedBytes(
            tempPath,
            probe.TotalBytes,
            probe.TotalBytes,
            sourceUri);

        progress?.Report(new DownloadProgress(
            probe.TotalBytes,
            probe.TotalBytes,
            100,
            $"Downloaded {FormatBytes(probe.TotalBytes)} of {FormatBytes(probe.TotalBytes)} (parallel)",
            stopwatch.Elapsed.TotalSeconds > 0 ? probe.TotalBytes / stopwatch.Elapsed.TotalSeconds : 0,
            TimeSpan.Zero));

        return true;
    }

    private static async Task<DownloadProbe?> TryProbeAsync(
        HttpClient httpClient,
        Uri sourceUri,
        CancellationToken cancellationToken)
    {
        DownloadProbe? rangedProbe = await TryProbeRangeAsync(httpClient, sourceUri, cancellationToken).ConfigureAwait(false);
        if (rangedProbe is not null)
        {
            return rangedProbe;
        }

        return await TryProbeHeadAsync(httpClient, sourceUri, cancellationToken).ConfigureAwait(false);
    }

    private static async Task<DownloadProbe?> TryProbeHeadAsync(
        HttpClient httpClient,
        Uri sourceUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, sourceUri);
        ModelDownloadHttpClientFactory.ApplyAuthentication(request);
        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        return TryCreateProbe(response);
    }

    private static async Task<DownloadProbe?> TryProbeRangeAsync(
        HttpClient httpClient,
        Uri sourceUri,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
        request.Headers.Range = new RangeHeaderValue(0, 0);
        ModelDownloadHttpClientFactory.ApplyAuthentication(request);
        using HttpResponseMessage response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.PartialContent &&
            response.Content.Headers.ContentRange?.Length is long rangedTotal &&
            rangedTotal > 0)
        {
            return new DownloadProbe(rangedTotal);
        }

        return TryCreateProbe(response);
    }

    private static DownloadProbe? TryCreateProbe(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        if (response.Content.Headers.ContentLength is not long totalBytes || totalBytes <= 0)
        {
            return null;
        }

        bool supportsRanges = response.Headers.AcceptRanges?.Contains("bytes") == true
            || response.Content.Headers.ContentRange is not null;
        return supportsRanges ? new DownloadProbe(totalBytes) : null;
    }

    private static async Task DownloadRangeAsync(
        HttpClient httpClient,
        Uri sourceUri,
        string tempPath,
        long start,
        long end,
        Action<long> reportBytesRead,
        CancellationToken cancellationToken)
    {
        const int bufferSize = 65536;
        long expectedBytes = end - start + 1;

        for (int attempt = 1; attempt <= RetryPolicy.Download.MaxAttempts; attempt++)
        {
            long rangeBytesRead = 0;
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, sourceUri);
                request.Headers.Range = new RangeHeaderValue(start, end);
                ModelDownloadHttpClientFactory.ApplyAuthentication(request);
                using HttpResponseMessage response = await httpClient
                    .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                    .ConfigureAwait(false);

                if (response.StatusCode != HttpStatusCode.PartialContent && response.StatusCode != HttpStatusCode.OK)
                {
                    if (DownloadRetry.ShouldRetryStatus(response.StatusCode) && attempt < RetryPolicy.Download.MaxAttempts)
                    {
                        await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    throw new HttpRequestException($"Range download failed with status {response.StatusCode} for {sourceUri}.");
                }

                await using Stream contentStream = await response.Content
                    .ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                await using var output = new FileStream(
                    tempPath,
                    FileMode.OpenOrCreate,
                    FileAccess.Write,
                    FileShare.ReadWrite,
                    bufferSize: bufferSize,
                    useAsync: true);
                output.Seek(start, SeekOrigin.Begin);

                byte[] buffer = new byte[bufferSize];
                int bytesRead;
                while ((bytesRead = await ReadAsyncWithStallTimeout(
                           contentStream,
                           buffer,
                           ReadStallTimeout,
                           cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await output.WriteAsync(buffer.AsMemory(0, bytesRead), cancellationToken).ConfigureAwait(false);
                    rangeBytesRead += bytesRead;
                    reportBytesRead(bytesRead);
                }

                if (rangeBytesRead != expectedBytes)
                {
                    throw new IOException(
                        $"Range download short read for {sourceUri} ({start}-{end}): expected {expectedBytes} bytes, got {rangeBytesRead}.");
                }

                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex) when (DownloadRetry.IsTransientException(ex, cancellationToken) && attempt < RetryPolicy.Download.MaxAttempts)
            {
                await Task.Delay(RetryPolicy.Download.GetDelay(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        throw new HttpRequestException($"Range download failed after retries for {sourceUri} ({start}-{end}).");
    }

    private static IEnumerable<(long Start, long End)> BuildRanges(long totalBytes, long chunkSizeBytes)
    {
        if (totalBytes <= 0)
        {
            yield break;
        }

        long chunkSize = Math.Max(chunkSizeBytes, 1);
        for (long start = 0; start < totalBytes; start += chunkSize)
        {
            long end = Math.Min(start + chunkSize - 1, totalBytes - 1);
            yield return (start, end);
        }
    }

    private static async Task<int> ReadAsyncWithStallTimeout(
        Stream stream,
        byte[] buffer,
        TimeSpan stallTimeout,
        CancellationToken cancellationToken)
    {
        using var stallCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        stallCts.CancelAfter(stallTimeout);

        try
        {
            return await stream.ReadAsync(buffer.AsMemory(0, buffer.Length), stallCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new IOException(
                $"Download stalled: no data received for {stallTimeout.TotalSeconds:F0}s.");
        }
    }

    private static long GetPartialDownloadLength(string tempPath)
    {
        try
        {
            return File.Exists(tempPath) ? new FileInfo(tempPath).Length : 0;
        }
        catch
        {
            return 0;
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

    private sealed record DownloadProbe(long TotalBytes);
}

