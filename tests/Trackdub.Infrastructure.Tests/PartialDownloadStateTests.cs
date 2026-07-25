using System.Net;
using System.Net.Http.Headers;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Licensing;
using Trackdub.Infrastructure.Logging;

namespace Trackdub.Infrastructure.Tests;

public sealed class PartialDownloadStateTests
{
    [Fact]
    public async Task PrepareResumeAsync_discards_legacy_partial_without_metadata()
    {
        string tempRoot = CreateTempRoot();
        Directory.CreateDirectory(tempRoot);
        string partialPath = Path.Combine(tempRoot, "model.onnx.partial");
        await File.WriteAllBytesAsync(partialPath, new byte[1024]);

        using var handler = new ResumeProbeHandler(totalBytes: 2048);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();
        var sourceUri = new Uri("https://huggingface.co/example/model/resolve/main/model.onnx");

        try
        {
            long resumeOffset = await PartialDownloadState.PrepareResumeAsync(
                httpClient,
                sourceUri,
                partialPath,
                logger,
                CancellationToken.None);

            Assert.Equal(0, resumeOffset);
            Assert.False(File.Exists(partialPath));
            Assert.False(File.Exists(PartialDownloadState.MetaPath(partialPath)));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task PrepareResumeAsync_discards_sparse_partial_when_committed_bytes_lag_file_length()
    {
        string tempRoot = CreateTempRoot();
        Directory.CreateDirectory(tempRoot);
        string partialPath = Path.Combine(tempRoot, "model.onnx.partial");
        await File.WriteAllBytesAsync(partialPath, new byte[2048]);
        var sourceUri = new Uri("https://huggingface.co/example/model/resolve/main/model.onnx");
        PartialDownloadState.RecordCommittedBytes(partialPath, 512, 2048, sourceUri);

        using var handler = new ResumeProbeHandler(totalBytes: 2048);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();

        try
        {
            long resumeOffset = await PartialDownloadState.PrepareResumeAsync(
                httpClient,
                sourceUri,
                partialPath,
                logger,
                CancellationToken.None);

            Assert.Equal(0, resumeOffset);
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task PrepareResumeAsync_returns_committed_offset_when_resume_probe_succeeds()
    {
        string tempRoot = CreateTempRoot();
        Directory.CreateDirectory(tempRoot);
        string partialPath = Path.Combine(tempRoot, "model.onnx.partial");
        byte[] committed = Enumerable.Range(0, 512).Select(i => (byte)i).ToArray();
        await File.WriteAllBytesAsync(partialPath, committed);
        var sourceUri = new Uri("https://huggingface.co/example/model/resolve/main/model.onnx");
        PartialDownloadState.RecordCommittedBytes(partialPath, committed.Length, 2048, sourceUri);

        using var handler = new ResumeProbeHandler(totalBytes: 2048);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();

        try
        {
            long resumeOffset = await PartialDownloadState.PrepareResumeAsync(
                httpClient,
                sourceUri,
                partialPath,
                logger,
                CancellationToken.None);

            Assert.Equal(committed.Length, resumeOffset);
            Assert.True(File.Exists(partialPath));
            Assert.True(File.Exists(PartialDownloadState.MetaPath(partialPath)));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    [Fact]
    public async Task HuggingFaceModelDownloader_discards_stale_partial_and_downloads_fresh()
    {
        byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray();
        using var handler = new RangeAwareHttpMessageHandler(payload);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();
        string tempRoot = CreateTempRoot();
        string cacheRoot = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(cacheRoot);

        var options = new HuggingFaceDownloadOptions
        {
            ParallelDownloadsEnabled = true,
            MaxParallelConnections = 4,
            MinFileSizeForParallelBytes = 128,
            ChunkSizeBytes = 128,
            CliPreference = HuggingFaceCliPreference.Never,
            DisableXet = true,
        };

        var downloader = new HuggingFaceModelDownloader(cacheRoot, logger, httpClient, options);
        string destinationPath = Path.Combine(cacheRoot, "example", "model.onnx");
        string stalePartialPath = $"{destinationPath}.partial";

        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        await File.WriteAllBytesAsync(stalePartialPath, new byte[256]);

        try
        {
            bool downloaded = await downloader.DownloadAsync(
                "example/model",
                "model.onnx",
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.False(File.Exists(stalePartialPath));
        }
        finally
        {
            DeleteTempRoot(tempRoot);
        }
    }

    private static string CreateTempRoot() =>
        Path.Combine(Path.GetTempPath(), "Trackdub.PartialDownloadState.Tests", Guid.NewGuid().ToString("N"));

    private static void DeleteTempRoot(string tempRoot)
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private sealed class ResumeProbeHandler(long totalBytes) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Headers.Range is { } range)
            {
                long start = range.Ranges.First().From ?? 0;
                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(Array.Empty<byte>()),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, totalBytes - 1, totalBytes);
                return Task.FromResult(response);
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(Array.Empty<byte>()),
            });
        }
    }

    private sealed class RangeAwareHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Head)
            {
                var head = new HttpResponseMessage(HttpStatusCode.OK);
                head.Headers.TryAddWithoutValidation("Accept-Ranges", "bytes");
                head.Content = new ByteArrayContent(Array.Empty<byte>());
                head.Content.Headers.ContentLength = payload.Length;
                return Task.FromResult(head);
            }

            if (request.Headers.Range is { } range)
            {
                long start = range.Ranges.First().From ?? 0;
                long? endInclusive = range.Ranges.First().To;
                long end = endInclusive ?? payload.Length - 1;
                int length = (int)(end - start + 1);
                byte[] slice = payload.AsSpan((int)start, length).ToArray();

                var response = new HttpResponseMessage(HttpStatusCode.PartialContent)
                {
                    Content = new ByteArrayContent(slice),
                };
                response.Content.Headers.ContentRange = new ContentRangeHeaderValue(start, end, payload.Length);
                return Task.FromResult(response);
            }

            var full = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            };
            full.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(full);
        }
    }
}
