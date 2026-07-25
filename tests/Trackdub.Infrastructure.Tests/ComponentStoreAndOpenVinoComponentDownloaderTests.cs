using System.IO.Compression;
using System.Security.Cryptography;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Components;

namespace Trackdub.Infrastructure.Tests;

public sealed class ComponentStoreAndOpenVinoComponentDownloaderTests
{
    [Fact]
    public void IsInstalled_WhenMarkerIsMissingAndFilesExist_WritesMarkerAndReturnsTrue()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.ComponentStore.Tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingApplicationLogger();
        var store = new ComponentStore(rootPath, logger);
        string componentPath = store.EnsureComponentDirectory("openvino");

        try
        {
            string tempMarkerPath = Path.Combine(componentPath, $".component-installed.{Guid.NewGuid():N}.tmp");
            File.WriteAllText(tempMarkerPath, "partial");

            Assert.True(store.IsInstalled("openvino"));
            Assert.True(File.Exists(Path.Combine(componentPath, ".component-installed")));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAndInstallAsync_ThrowsWhenIntegrityMetadataIsMissingAndInsecureDownloadsAreDisabled()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.OpenVinoDownloader.Tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingApplicationLogger();
        var store = new ComponentStore(rootPath, logger);
        var handler = new ThrowingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var downloader = new OpenVinoComponentDownloader(
            store,
            httpClient,
            logger,
            new OpenVinoComponentSettings
            {
                DownloadUrl = "https://example.test/openvino-runtime.zip"
            },
            allowInsecureComponentDownload: false);

        try
        {
            OpenVinoComponentException exception = await Assert.ThrowsAsync<OpenVinoComponentException>(() =>
                downloader.DownloadAndInstallAsync());

            Assert.Contains("integrity metadata is required", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAndInstallAsync_ThrowsWhenIntegrityMetadataIsMissingEvenWhenInsecureOverrideEnabled()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.OpenVinoDownloader.Tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingApplicationLogger();
        var store = new ComponentStore(rootPath, logger);
        var handler = new ThrowingHttpMessageHandler();
        using var httpClient = new HttpClient(handler);
        var downloader = new OpenVinoComponentDownloader(
            store,
            httpClient,
            logger,
            new OpenVinoComponentSettings
            {
                DownloadUrl = "https://example.test/openvino-runtime.zip"
            },
            allowInsecureComponentDownload: true);

        try
        {
            OpenVinoComponentException exception = await Assert.ThrowsAsync<OpenVinoComponentException>(() =>
                downloader.DownloadAndInstallAsync());

            Assert.Contains("integrity metadata is required", exception.Message, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, handler.RequestCount);
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    [Fact]
    public async Task DownloadAndInstallAsync_WithSingleIntegritySignal_LogsWarningAndMarksInstalled()
    {
        string rootPath = Path.Combine(Path.GetTempPath(), "Trackdub.OpenVinoDownloader.Tests", Guid.NewGuid().ToString("N"));
        var logger = new RecordingApplicationLogger();
        var store = new ComponentStore(rootPath, logger);
        byte[] archiveBytes = CreateComponentArchiveBytes();
        string expectedHash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
        using var handler = new StaticHttpMessageHandler(archiveBytes);
        using var httpClient = new HttpClient(handler);
        var downloader = new OpenVinoComponentDownloader(
            store,
            httpClient,
            logger,
            new OpenVinoComponentSettings
            {
                DownloadUrl = "https://example.test/openvino-runtime.zip",
                ExpectedSha256Hash = expectedHash
            },
            allowInsecureComponentDownload: true);

        try
        {
            string installPath = await downloader.DownloadAndInstallAsync();

            Assert.True(Directory.Exists(installPath));
            Assert.True(store.IsInstalled(OpenVinoComponentDownloader.ComponentId));
            Assert.True(File.Exists(Path.Combine(installPath, ".component-installed")));
            Assert.Contains(
                logger.Warnings,
                warning => warning.Contains("single metadata source", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            if (Directory.Exists(rootPath))
            {
                Directory.Delete(rootPath, recursive: true);
            }
        }
    }

    private static byte[] CreateComponentArchiveBytes()
    {
        using var memory = new MemoryStream();
        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            ZipArchiveEntry entry = archive.CreateEntry("runtime/bin/openvino.dll");
            using StreamWriter writer = new(entry.Open());
            writer.Write("fake-openvino-runtime");
        }

        return memory.ToArray();
    }

    private sealed class ThrowingHttpMessageHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestCount++;
            throw new HttpRequestException("HTTP should not be called when integrity metadata is missing.");
        }
    }

    private sealed class StaticHttpMessageHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload)
            };
            response.Content.Headers.ContentLength = payload.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class RecordingApplicationLogger : IApplicationLogger
    {
        public List<string> Warnings { get; } = [];

        public void LogDebug(string message)
        {
        }

        public void LogInformation(string message)
        {
        }

        public void LogWarning(string message, Exception? exception = null)
        {
            Warnings.Add(message);
        }

        public void LogError(string message, Exception? exception = null)
        {
        }
    }
}
