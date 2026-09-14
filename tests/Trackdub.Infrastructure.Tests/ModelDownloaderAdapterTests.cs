using System.Net;
using System.Net.Http.Headers;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Licensing;

namespace Trackdub.Infrastructure.Tests;

public sealed class ModelDownloaderAdapterTests
{
    [Fact]
    public async Task DownloadUriAsync_writes_successful_runtime_support_file()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = [1, 2, 3, 4];
        var logger = new RecordingApplicationLogger();
        using var handler = new StaticResponseHandler(HttpStatusCode.OK, payload);
        using var httpClient = new HttpClient(handler, disposeHandler: true);
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "weya_nc.dll");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.tmp"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_logs_http_failure_for_runtime_support_file()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingApplicationLogger();
        using var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.NotFound));
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "weya_nc.dll");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath);

            Assert.False(downloaded);
            Assert.False(File.Exists(destinationPath));
            Assert.Contains(logger.Errors, error => error.Contains("NotFound", StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_resumes_after_transient_stream_failure()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = [1, 2, 3, 4];
        var logger = new RecordingApplicationLogger();
        using var httpClient = new HttpClient(new ResumableFailureHandler(payload, failAfterBytes: 2));
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "models", "decoder_model_quantized.onnx");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/madlad/decoder_model_quantized.onnx"),
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.partial"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_restarts_from_zero_when_partial_range_is_not_satisfiable()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] stalePartial = [9, 9, 9, 9];
        byte[] payload = [1, 2, 3, 4, 5];
        var logger = new RecordingApplicationLogger();
        using var httpClient = new HttpClient(new StalePartialThenSuccessHandler(stalePartial.Length, payload));
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "models", "decoder_model_quantized.onnx");
        string partialPath = $"{destinationPath}.partial";

        try
        {
            var sourceUri = new Uri("https://example.test/madlad/decoder_model_quantized.onnx");
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(partialPath, stalePartial);
            PartialDownloadState.RecordCommittedBytes(partialPath, stalePartial.Length, payload.Length, sourceUri);

            bool downloaded = await adapter.DownloadUriAsync(
                sourceUri,
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.False(File.Exists(partialPath));
            Assert.Contains(
                logger.Warnings,
                warning => warning.Contains("legacy partial", StringComparison.OrdinalIgnoreCase)
                    || warning.Contains("non-resumable", StringComparison.OrdinalIgnoreCase)
                    || warning.Contains("restarting", StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(logger.Errors, error => error.Contains(nameof(HttpStatusCode.RequestedRangeNotSatisfiable), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_removes_partial_file_after_terminal_exception_failure()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = [1, 2, 3, 4];
        var logger = new RecordingApplicationLogger();
        using var httpClient = new HttpClient(new PartialThenTerminalFailureHandler(payload, failAfterBytes: 2));
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "runtime", "support", "weya_nc.dll");
        string partialPath = $"{destinationPath}.partial";

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath);

            Assert.False(downloaded);
            Assert.False(File.Exists(destinationPath));
            Assert.False(File.Exists(partialPath));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_downloads_large_runtime_support_file_across_multiple_buffers()
    {
        const int payloadLength = 196_608;
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = new byte[payloadLength];
        for (int i = 0; i < payload.Length; i++)
        {
            payload[i] = (byte)(i % 251);
        }

        var logger = new RecordingApplicationLogger();
        using var httpClient = new HttpClient(new StaticResponseHandler(HttpStatusCode.OK, payload));
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "large_support.bin");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/large_support.bin"),
                destinationPath);

            Assert.True(downloaded);
            byte[] written = await File.ReadAllBytesAsync(destinationPath);
            Assert.Equal(payloadLength, written.Length);
            Assert.Equal(payload, written);
            Assert.Equal((byte)0, written[0]);
            Assert.Equal((byte)(payloadLength / 2 % 251), written[payloadLength / 2]);
            Assert.Equal((byte)((payloadLength - 1) % 251), written[^1]);
            Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(destinationPath)!, "*.partial"));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_retries_after_http_request_timeout_status()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = [10, 20, 30, 40];
        var logger = new RecordingApplicationLogger();
        var handler = new TimeoutThenSuccessHandler(payload);
        using var httpClient = new HttpClient(handler);
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "weya_nc.dll");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.Contains(logger.Errors, error => error.Contains(nameof(HttpStatusCode.RequestTimeout), StringComparison.Ordinal));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_retries_after_http_request_exception_on_send()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = [5, 6, 7, 8];
        var logger = new RecordingApplicationLogger();
        var handler = new HttpRequestExceptionThenSuccessHandler(payload);
        using var httpClient = new HttpClient(handler);
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "weya_nc.dll");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.Contains(logger.Warnings, warning => warning.Contains("Retrying attempt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_retries_after_client_timeout_without_user_cancellation()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        byte[] payload = [11, 22, 33, 44];
        var logger = new RecordingApplicationLogger();
        var handler = new ClientTimeoutThenSuccessHandler(payload);
        using var httpClient = new HttpClient(handler);
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "weya_nc.dll");

        try
        {
            bool downloaded = await adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(2, handler.RequestCount);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.Contains(logger.Warnings, warning => warning.Contains("Retrying attempt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadUriAsync_does_not_retry_when_user_cancels()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloaderAdapter.Tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingApplicationLogger();
        var handler = new CancellationAwareSlowHandler(TimeSpan.FromSeconds(30));
        using var httpClient = new HttpClient(handler);
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, logger);
        string destinationPath = Path.Combine(tempRoot, "deployment", "lib", "weya_nc.dll");
        using var cts = new CancellationTokenSource();

        try
        {
            Task<bool> downloadTask = adapter.DownloadUriAsync(
                new Uri("https://example.test/hush/weya_nc.dll"),
                destinationPath,
                cancellationToken: cts.Token);
            cts.Cancel();

            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => downloadTask);
            Assert.Equal(1, handler.RequestCount);
            Assert.DoesNotContain(logger.Warnings, warning => warning.Contains("Retrying attempt", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task Dispose_disposes_adapter_owned_http_client()
    {
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), logger: new RecordingApplicationLogger());
        HttpClient httpClient = GetHttpClient(adapter);

        Assert.IsAssignableFrom<IDisposable>(adapter).Dispose();

        await Assert.ThrowsAsync<ObjectDisposedException>(() => httpClient.GetAsync("http://127.0.0.1:1/"));
    }

    [Fact]
    public async Task Dispose_does_not_dispose_caller_owned_http_client()
    {
        var handler = new TrackingHandler();
        using var httpClient = new HttpClient(handler);
        var adapter = new ModelDownloaderAdapter(new FakeModelDownloader(), httpClient, new RecordingApplicationLogger());

        Assert.IsAssignableFrom<IDisposable>(adapter).Dispose();

        Assert.False(handler.IsDisposed);
        using HttpResponseMessage response = await httpClient.GetAsync("https://example.test/runtime-support");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadAsync_throws_object_disposed_after_dispose(bool ownsHttpClient)
    {
        var innerDownloader = new FakeModelDownloader();
        using var callerOwnedHttpClient = new HttpClient(new TrackingHandler());
        var adapter = ownsHttpClient
            ? new ModelDownloaderAdapter(innerDownloader, logger: new RecordingApplicationLogger())
            : new ModelDownloaderAdapter(innerDownloader, callerOwnedHttpClient, new RecordingApplicationLogger());

        adapter.Dispose();

        ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.DownloadAsync("model-id", "weights.onnx", "C:\\temp\\weights.onnx"));
        Assert.Equal(typeof(ModelDownloaderAdapter).FullName, exception.ObjectName);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DownloadUriAsync_throws_object_disposed_after_dispose(bool ownsHttpClient)
    {
        using var callerOwnedHttpClient = new HttpClient(new TrackingHandler());
        var adapter = ownsHttpClient
            ? new ModelDownloaderAdapter(new FakeModelDownloader(), logger: new RecordingApplicationLogger())
            : new ModelDownloaderAdapter(new FakeModelDownloader(), callerOwnedHttpClient, new RecordingApplicationLogger());

        adapter.Dispose();

        ObjectDisposedException exception = await Assert.ThrowsAsync<ObjectDisposedException>(() =>
            adapter.DownloadUriAsync(
                new Uri("https://example.test/runtime-support"),
                Path.Combine(Path.GetTempPath(), "runtime-support.dll")));
        Assert.Equal(typeof(ModelDownloaderAdapter).FullName, exception.ObjectName);
    }

    private static HttpClient GetHttpClient(ModelDownloaderAdapter adapter)
    {
        var field = typeof(ModelDownloaderAdapter).GetField("httpClient", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        Assert.NotNull(field);
        return Assert.IsType<HttpClient>(field.GetValue(adapter));
    }

    private sealed class StaticResponseHandler(HttpStatusCode statusCode, byte[]? payload = null) : HttpMessageHandler
    {
        private readonly byte[] payload = payload ?? [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            });
    }

    private sealed class ResumableFailureHandler(byte[] payload, int failAfterBytes) : HttpMessageHandler
    {
        private int callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ThrowingByteArrayContent(payload, failAfterBytes)
                });
            }

            if (request.Headers.Range is null)
            {
                throw new Xunit.Sdk.XunitException("Expected resume range request after partial download.");
            }

            Assert.Equal(new RangeHeaderValue(failAfterBytes, null).ToString(), request.Headers.Range.ToString());
            byte[] remaining = payload.Skip(failAfterBytes).ToArray();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.PartialContent)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(remaining)
                {
                    Headers =
                    {
                        ContentRange = new ContentRangeHeaderValue(
                            failAfterBytes,
                            payload.Length - 1,
                            payload.Length)
                    }
                }
            });
        }
    }

    private sealed class StalePartialThenSuccessHandler(int expectedStaleBytes, byte[] payload) : HttpMessageHandler
    {
        private int callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount == 1 &&
                request.Headers.Range is { } resumeRange &&
                resumeRange.ToString() == new RangeHeaderValue(expectedStaleBytes, null).ToString())
            {
                return Task.FromResult(CreateResponse(
                    HttpStatusCode.RequestedRangeNotSatisfiable,
                    request,
                    new ByteArrayContent([])));
            }

            if (request.Headers.Range is not null)
            {
                throw new Xunit.Sdk.XunitException($"Unexpected range request on call {callCount}: {request.Headers.Range}.");
            }

            return Task.FromResult(CreateResponse(
                HttpStatusCode.OK,
                request,
                new ByteArrayContent(payload)));
        }

        private static HttpResponseMessage CreateResponse(
            HttpStatusCode statusCode,
            HttpRequestMessage request,
            HttpContent content)
        {
            return new HttpResponseMessage(statusCode)
            {
                RequestMessage = request,
                Content = content
            };
        }
    }

    private sealed class TimeoutThenSuccessHandler(byte[] payload) : HttpMessageHandler
    {
        private int callCount;

        public int RequestCount => callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount == 1)
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.RequestTimeout)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent([])
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class HttpRequestExceptionThenSuccessHandler(byte[] payload) : HttpMessageHandler
    {
        private int callCount;

        public int RequestCount => callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount == 1)
            {
                throw new HttpRequestException("Simulated request timeout.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class ClientTimeoutThenSuccessHandler(byte[] payload) : HttpMessageHandler
    {
        private int callCount;

        public int RequestCount => callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            if (callCount == 1 && !cancellationToken.IsCancellationRequested)
            {
                throw new TaskCanceledException("Simulated HttpClient.Timeout.");
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(payload)
            });
        }
    }

    private sealed class CancellationAwareSlowHandler(TimeSpan slowDelay) : HttpMessageHandler
    {
        private int callCount;

        public int RequestCount => callCount;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            await Task.Delay(slowDelay, cancellationToken).ConfigureAwait(false);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            };
        }
    }

    private sealed class PartialThenTerminalFailureHandler(byte[] payload, int failAfterBytes) : HttpMessageHandler
    {
        private int callCount;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            callCount++;
            return callCount switch
            {
                1 => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ThrowingByteArrayContent(payload, failAfterBytes)
                }),
                2 => throw new InvalidOperationException("Simulated terminal failure."),
                _ => throw new Xunit.Sdk.XunitException("Unexpected additional retry.")
            };
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        private readonly System.Collections.Generic.List<HttpResponseMessage> responses = [];
        public bool IsDisposed { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent([])
            };

            responses.Add(response);
            return Task.FromResult(response);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (var response in responses)
                {
                    response.Dispose();
                }

                responses.Clear();
            }

            IsDisposed = true;
            base.Dispose(disposing);
        }
    }

    private sealed class ThrowingByteArrayContent : HttpContent
    {
        private readonly byte[] payload;
        private readonly int failAfterBytes;

        public ThrowingByteArrayContent(byte[] payload, int failAfterBytes)
        {
            this.payload = payload;
            this.failAfterBytes = failAfterBytes;
            Headers.ContentLength = payload.Length;
        }

        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
        {
            throw new NotSupportedException();
        }

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            Task.FromResult<Stream>(new ThrowingReadStream(payload, failAfterBytes));

        protected override bool TryComputeLength(out long length)
        {
            length = payload.Length;
            return true;
        }
    }

    private sealed class ThrowingReadStream(byte[] payload, int failAfterBytes) : MemoryStream(payload)
    {
        private bool hasThrown;

        public override Task<int> ReadAsync(
            byte[] buffer,
            int offset,
            int count,
            CancellationToken cancellationToken)
        {
            return Task.FromResult(ReadCore(buffer.AsSpan(offset, count), cancellationToken));
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            return new ValueTask<int>(ReadCore(buffer.Span, cancellationToken));
        }

        private int ReadCore(Span<byte> destination, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!hasThrown && Position >= failAfterBytes)
            {
                hasThrown = true;
                throw new IOException("Simulated transient stream failure.");
            }

            int safeCount = hasThrown
                ? destination.Length
                : Math.Min(destination.Length, Math.Max(0, failAfterBytes - (int)Position));
            return Read(destination[..safeCount]);
        }
    }

    private sealed class FakeModelDownloader : IModelDownloader
    {
        public Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<DownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null) =>
            Task.FromResult(false);

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }

    private sealed class RecordingApplicationLogger : IApplicationLogger
    {
        private readonly List<string> errors = [];
        private readonly List<string> warnings = [];

        public IReadOnlyList<string> Errors => errors;

        public IReadOnlyList<string> Warnings => warnings;

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message)
        {
        }

        public void LogWarning(string message, Exception? exception = null)
        {
            warnings.Add(exception is null ? message : $"{message}: {exception.Message}");
        }

        public void LogError(string message, Exception? exception = null)
        {
            errors.Add(exception is null ? message : $"{message}: {exception.Message}");
        }
    }
}
