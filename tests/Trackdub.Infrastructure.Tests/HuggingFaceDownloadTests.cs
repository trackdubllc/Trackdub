using System.Net;
using System.Net.Http.Headers;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Licensing;
using Trackdub.Infrastructure.Logging;

namespace Trackdub.Infrastructure.Tests;

[Collection(nameof(HuggingFaceEnvCollection))]
public sealed class HuggingFaceDownloadTests
{
    [Fact]
    public void FromEnvironment_reads_trackdub_and_hub_variables()
    {
        using var env = new EnvironmentOverride();
        env.Set(HuggingFaceDownloadOptions.ParallelDownloadsEnv, "0");
        env.Set(HuggingFaceDownloadOptions.DisableXetEnv, "0");
        env.Set(HuggingFaceDownloadOptions.MaxConnectionsEnv, "12");
        env.Set(HuggingFaceDownloadOptions.UseCliEnv, "never");
        env.Set(HuggingFaceDownloadOptions.CliTransferEnv, null);

        HuggingFaceDownloadOptions options = HuggingFaceDownloadOptions.FromEnvironment();

        Assert.False(options.ParallelDownloadsEnabled);
        Assert.False(options.DisableXet);
        Assert.False(options.EnableCliTransfer);
        Assert.Equal(12, options.MaxParallelConnections);
        Assert.Equal(HuggingFaceCliPreference.Never, options.CliPreference);

        IReadOnlyDictionary<string, string> hubEnv = options.BuildHubCliEnvironmentVariables();
        Assert.Equal("1", hubEnv[HuggingFaceDownloadOptions.PythonUtf8Env]);
        Assert.Equal("utf-8", hubEnv[HuggingFaceDownloadOptions.PythonIoEncodingEnv]);
        Assert.Equal("1", hubEnv[HuggingFaceDownloadOptions.HubDisableProgressBarsEnv]);
        Assert.Equal("0", hubEnv[HuggingFaceDownloadOptions.HubEnableTransferEnv]);
        Assert.False(hubEnv.ContainsKey(HuggingFaceDownloadOptions.HubDisableXetEnv));
    }

    [Fact]
    public void BuildHubCliEnvironmentVariables_includes_utf8_and_disables_transfer_by_default()
    {
        var options = new HuggingFaceDownloadOptions
        {
            ParallelDownloadsEnabled = true,
            DisableXet = true,
            EnableCliTransfer = false,
        };

        IReadOnlyDictionary<string, string> env = options.BuildHubCliEnvironmentVariables();

        Assert.Equal("1", env[HuggingFaceDownloadOptions.PythonUtf8Env]);
        Assert.Equal("utf-8", env[HuggingFaceDownloadOptions.PythonIoEncodingEnv]);
        Assert.Equal("1", env[HuggingFaceDownloadOptions.HubDisableProgressBarsEnv]);
        Assert.Equal("1", env[HuggingFaceDownloadOptions.HubDisableXetEnv]);
        Assert.Equal("0", env[HuggingFaceDownloadOptions.HubEnableTransferEnv]);
    }

    [Fact]
    public void BuildHubCliEnvironmentVariables_enables_transfer_only_when_opted_in()
    {
        var options = new HuggingFaceDownloadOptions
        {
            ParallelDownloadsEnabled = true,
            EnableCliTransfer = true,
            DisableXet = false,
        };

        IReadOnlyDictionary<string, string> env = options.BuildHubCliEnvironmentVariables();

        Assert.Equal("1", env[HuggingFaceDownloadOptions.HubEnableTransferEnv]);
        Assert.False(env.ContainsKey(HuggingFaceDownloadOptions.HubDisableXetEnv));
    }

    [Fact]
    public void FromEnvironment_reads_cli_transfer_opt_in()
    {
        using var env = new EnvironmentOverride();
        env.Set(HuggingFaceDownloadOptions.CliTransferEnv, "1");

        HuggingFaceDownloadOptions options = HuggingFaceDownloadOptions.FromEnvironment();
        Assert.True(options.EnableCliTransfer);
        Assert.Equal(
            "1",
            options.BuildHubCliEnvironmentVariables()[HuggingFaceDownloadOptions.HubEnableTransferEnv]);
    }

    [Fact]
    public void CliDownloader_sticky_disables_after_encoding_failure()
    {
        var logger = new DebugApplicationLogger();
        var downloader = new HuggingFaceCliDownloader(
            new HuggingFaceDownloadOptions { CliPreference = HuggingFaceCliPreference.Auto },
            logger);

        downloader.RecordFailureForTests(
            "Error: Invalid value. 'charmap' codec can't encode character '\\u2713'");

        Assert.True(downloader.IsStickyDisabled);
        Assert.False(downloader.ShouldAttempt);
    }

    [Fact]
    public void CliDownloader_sticky_disables_after_consecutive_failures()
    {
        var logger = new DebugApplicationLogger();
        var downloader = new HuggingFaceCliDownloader(
            new HuggingFaceDownloadOptions { CliPreference = HuggingFaceCliPreference.Auto },
            logger);

        downloader.RecordFailureForTests("temporary network glitch");
        Assert.False(downloader.IsStickyDisabled);

        downloader.RecordFailureForTests("temporary network glitch");
        Assert.True(downloader.IsStickyDisabled);
        Assert.False(downloader.ShouldAttempt);
    }

    [Theory]
    [InlineData("Error: 'charmap' codec can't encode character")]
    [InlineData("UnicodeEncodeError: something")]
    [InlineData("codec can't decode byte")]
    public void IsEncodingFailureLine_detects_known_patterns(string line) =>
        Assert.True(HuggingFaceCliDownloader.IsEncodingFailureLine(line));

    [Fact]
    public void CliDownloader_sticky_disable_message_refuses_http_when_required()
    {
        var logger = new CapturingApplicationLogger();
        var downloader = new HuggingFaceCliDownloader(
            new HuggingFaceDownloadOptions { CliPreference = HuggingFaceCliPreference.Required },
            logger);

        downloader.RecordFailureForTests(
            "Error: Invalid value. 'charmap' codec can't encode character '\\u2713'");

        Assert.True(downloader.IsStickyDisabled);
        Assert.Contains(
            logger.Warnings,
            message => message.Contains("HTTP fallback will be refused", StringComparison.Ordinal));
    }

    [Fact]
    public void CleanupStaleTempDirectories_removes_old_orphans()
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.HfCli.Tests",
            Guid.NewGuid().ToString("N"));
        string stale = Path.Combine(root, "stale");
        string fresh = Path.Combine(root, "fresh");
        Directory.CreateDirectory(stale);
        Directory.CreateDirectory(fresh);
        Directory.SetLastWriteTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        Directory.SetCreationTimeUtc(stale, DateTime.UtcNow - TimeSpan.FromDays(2));
        Directory.SetLastWriteTimeUtc(fresh, DateTime.UtcNow);
        Directory.SetCreationTimeUtc(fresh, DateTime.UtcNow);

        try
        {
            HuggingFaceCliDownloader.CleanupStaleTempDirectories(
                new DebugApplicationLogger(),
                TimeSpan.FromHours(6),
                root);

            Assert.False(Directory.Exists(stale));
            Assert.True(Directory.Exists(fresh));
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ParallelRangeDownloader_downloads_file_with_multiple_ranges()
    {
        byte[] payload = Enumerable.Range(0, 256).Select(i => (byte)i).ToArray();
        using var handler = new RangeAwareHttpMessageHandler(payload);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.HuggingFaceDownload.Tests", Guid.NewGuid().ToString("N"));
        string tempPath = Path.Combine(tempRoot, "model.onnx.partial");
        Directory.CreateDirectory(tempRoot);

        try
        {
            var options = new HuggingFaceDownloadOptions
            {
                ParallelDownloadsEnabled = true,
                MaxParallelConnections = 4,
                MinFileSizeForParallelBytes = 64,
                ChunkSizeBytes = 64,
            };

            bool downloaded = await ParallelRangeDownloader.TryDownloadAsync(
                httpClient,
                new Uri("https://huggingface.co/example/model/resolve/main/model.onnx?download=true"),
                tempPath,
                options,
                logger,
                progress: null,
                CancellationToken.None);

            Assert.True(downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(tempPath));
            Assert.True(handler.RangeRequestCount >= 4);
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
    public async Task HuggingFaceModelDownloader_required_cli_refuses_http_fallback()
    {
        byte[] payload = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        using var handler = new RangeAwareHttpMessageHandler(payload);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.HuggingFaceDownload.Tests", Guid.NewGuid().ToString("N"));
        string cacheRoot = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(cacheRoot);

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        try
        {
            var options = new HuggingFaceDownloadOptions
            {
                ParallelDownloadsEnabled = true,
                MaxParallelConnections = 4,
                MinFileSizeForParallelBytes = 32,
                ChunkSizeBytes = 32,
                CliPreference = HuggingFaceCliPreference.Required,
                DisableXet = true,
            };

            var downloader = new HuggingFaceModelDownloader(cacheRoot, logger, httpClient, options);
            string destinationPath = Path.Combine(cacheRoot, "example", "model.onnx");

            bool downloaded = await downloader.DownloadAsync(
                "example/model",
                "model.onnx",
                destinationPath);

            Assert.False(downloaded);
            Assert.False(File.Exists(destinationPath));
            Assert.Equal(0, handler.TotalRequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HuggingFaceModelDownloader_required_cli_with_existing_partial_makes_zero_http_requests()
    {
        byte[] payload = Enumerable.Range(0, 128).Select(i => (byte)i).ToArray();
        using var handler = new RangeAwareHttpMessageHandler(payload);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.HuggingFaceDownload.Tests", Guid.NewGuid().ToString("N"));
        string cacheRoot = Path.Combine(tempRoot, "cache");
        Directory.CreateDirectory(cacheRoot);

        string destinationPath = Path.Combine(cacheRoot, "example", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        string partialPath = $"{destinationPath}.partial";
        await File.WriteAllBytesAsync(partialPath, payload.AsSpan(0, 32).ToArray());

        string? originalPath = Environment.GetEnvironmentVariable("PATH");
        Environment.SetEnvironmentVariable("PATH", string.Empty);

        try
        {
            var options = new HuggingFaceDownloadOptions
            {
                ParallelDownloadsEnabled = true,
                MaxParallelConnections = 4,
                MinFileSizeForParallelBytes = 32,
                ChunkSizeBytes = 32,
                CliPreference = HuggingFaceCliPreference.Required,
                DisableXet = true,
            };

            var downloader = new HuggingFaceModelDownloader(cacheRoot, logger, httpClient, options);

            bool downloaded = await downloader.DownloadAsync(
                "example/model",
                "model.onnx",
                destinationPath);

            Assert.False(downloaded);
            Assert.False(File.Exists(destinationPath));
            Assert.False(File.Exists(partialPath));
            Assert.Equal(0, handler.TotalRequestCount);
        }
        finally
        {
            Environment.SetEnvironmentVariable("PATH", originalPath);
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task HuggingFaceModelDownloader_uses_parallel_path_for_large_files()
    {
        byte[] payload = Enumerable.Range(0, 512).Select(i => (byte)(i % 251)).ToArray();
        using var handler = new RangeAwareHttpMessageHandler(payload);
        using var httpClient = new HttpClient(handler);
        var logger = new DebugApplicationLogger();
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.HuggingFaceDownload.Tests", Guid.NewGuid().ToString("N"));
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

        try
        {
            bool downloaded = await downloader.DownloadAsync(
                "example/model",
                "model.onnx",
                destinationPath);

            Assert.True(downloaded);
            Assert.Equal(payload, await File.ReadAllBytesAsync(destinationPath));
            Assert.True(handler.RangeRequestCount >= 2);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class EnvironmentOverride : IDisposable
    {
        private readonly List<(string Name, string? Original)> saved = [];

        public void Set(string name, string? value)
        {
            saved.Add((name, Environment.GetEnvironmentVariable(name)));
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            for (int index = saved.Count - 1; index >= 0; index--)
            {
                (string name, string? original) = saved[index];
                Environment.SetEnvironmentVariable(name, original);
            }

            saved.Clear();
        }
    }

    private sealed class CapturingApplicationLogger : IApplicationLogger
    {
        public List<string> Warnings { get; } = [];

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message)
        {
        }

        public void LogWarning(string message, Exception? exception = null) =>
            Warnings.Add(message);

        public void LogError(string message, Exception? exception = null)
        {
        }

        public void LogErrorSynchronously(string message, Exception? exception = null)
        {
        }

        public void Flush()
        {
        }

        public void Flush(TimeSpan timeout)
        {
        }
    }

    private sealed class RangeAwareHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        private int _rangeRequestCount;
        private int _totalRequestCount;

        public int RangeRequestCount => Volatile.Read(ref _rangeRequestCount);

        public int TotalRequestCount => Volatile.Read(ref _totalRequestCount);

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _totalRequestCount);

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
                // ParallelRangeDownloader issues concurrent range GETs; count must be atomic.
                Interlocked.Increment(ref _rangeRequestCount);
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
