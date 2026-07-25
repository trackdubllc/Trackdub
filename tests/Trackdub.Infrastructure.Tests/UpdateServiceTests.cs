using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Trackdub.Application.Updates;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Infrastructure.Updates;

namespace Trackdub.Infrastructure.Tests;

public sealed class UpdateServiceTests
{
    private static readonly Uri TestManifestUrl = new("https://releases.trackdub.ai/manifest.json");
    private static readonly Uri TestDownloadUrl = new("https://releases.trackdub.ai/downloads/Trackdub-2.0.0-setup.exe");

    [Fact]
    public async Task CheckForUpdateAsync_UpdateAvailable_ReturnsUpdateResult()
    {
        var manifest = new ReleaseManifestSchema(
            LatestVersion: "2.0.0",
            DownloadUrl: TestDownloadUrl.ToString(),
            Sha256: "a".Repeat(64),
            ReleaseNotesUrl: "https://releases.trackdub.ai/2.0.0",
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        using var handler = new StaticHttpMessageHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(manifest));

        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.CheckForUpdateAsync("1.0.0");

        Assert.True(result.UpdateAvailable);
        Assert.NotNull(result.Release);
        Assert.Equal("2.0.0", result.Release.Version);
        Assert.Equal(TestDownloadUrl, result.Release.DownloadUrl);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdateAsync_SameVersion_ReturnsNoUpdate()
    {
        var manifest = new ReleaseManifestSchema(
            LatestVersion: "1.0.0",
            DownloadUrl: TestDownloadUrl.ToString(),
            Sha256: "a".Repeat(64),
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        using var handler = new StaticHttpMessageHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(manifest));

        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.CheckForUpdateAsync("1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdateAsync_CurrentNewerThanLatest_ReturnsNoUpdate()
    {
        var manifest = new ReleaseManifestSchema(
            LatestVersion: "1.0.0",
            DownloadUrl: TestDownloadUrl.ToString(),
            Sha256: "a".Repeat(64),
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        using var handler = new StaticHttpMessageHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(manifest));

        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.CheckForUpdateAsync("2.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.Null(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkError_ReturnsError()
    {
        using var handler = new ThrowingHttpMessageHandler(new HttpRequestException("Connection refused"));
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.CheckForUpdateAsync("1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.NotNull(result.ErrorMessage);
        Assert.Contains("Connection refused", result.ErrorMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task CheckForUpdateAsync_InvalidJson_ReturnsError()
    {
        using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, "not-json{}broken");
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.CheckForUpdateAsync("1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdateAsync_InvalidDownloadUrl_ReturnsError()
    {
        var manifest = new ReleaseManifestSchema(
            LatestVersion: "2.0.0",
            DownloadUrl: "not-a-valid-url",
            Sha256: "a".Repeat(64),
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        using var handler = new StaticHttpMessageHandler(
            HttpStatusCode.OK,
            JsonSerializer.Serialize(manifest));

        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        var result = await service.CheckForUpdateAsync("1.0.0");

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_EmptyManifest_ReturnsNoUpdateWithCurrentVersion()
    {
        const string manifestJson = """
        {
          "latestVersion": null,
          "releaseNotesUrl": null,
          "downloadUrl": null,
          "releaseDate": null
        }
        """;

        using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, manifestJson);
        using var httpClient = new HttpClient(handler);
        var service = new ReleaseManifestUpdateService(httpClient);

        Contracts.UpdateCheckResult result = await service.CheckForUpdatesAsync(UpdateChannel.Stable, "1.2.3");

        Assert.False(result.IsUpdateAvailable);
        Assert.Equal("1.2.3", result.AvailableVersion);
        Assert.Equal(UpdateChannel.Stable, result.Channel);
    }

    [Fact]
    public async Task CheckForUpdatesAsync_SemVerSuffix_ReturnsUpdateResult()
    {
        const string manifestJson = """
        {
          "latestVersion": "2.0.1-beta.1",
          "releaseNotesUrl": "https://releases.trackdub.ai/2.0.1-beta.1",
          "downloadUrl": "https://releases.trackdub.ai/downloads/Trackdub-2.0.1-beta.1-setup.exe",
          "releaseDate": "2026-06-01T00:00:00Z"
        }
        """;

        using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, manifestJson);
        using var httpClient = new HttpClient(handler);
        var service = new ReleaseManifestUpdateService(httpClient);

        Contracts.UpdateCheckResult result = await service.CheckForUpdatesAsync(UpdateChannel.Preview, "2.0.0");

        Assert.True(result.IsUpdateAvailable);
        Assert.Equal("2.0.1-beta.1", result.AvailableVersion);
        Assert.Equal(UpdateChannel.Preview, result.Channel);
    }

    [Fact]
    public async Task CheckForUpdateAsync_Cancelled_ReturnsCancelledError()
    {
        var manifest = new ReleaseManifestSchema(
            LatestVersion: "2.0.0",
            DownloadUrl: TestDownloadUrl.ToString(),
            Sha256: "a".Repeat(64),
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow,
            IsPrerelease: false);

        using var cts = new CancellationTokenSource();
        using var handler = new CancellableHttpMessageHandler(cts);
        using var httpClient = new HttpClient(handler);
        var service = CreateService(httpClient);

        cts.Cancel();

        var result = await service.CheckForUpdateAsync("1.0.0", cts.Token);

        Assert.False(result.UpdateAvailable);
        Assert.Null(result.Release);
        Assert.NotNull(result.ErrorMessage);
    }

    [Fact]
    public async Task DownloadUpdateAsync_Success_DownloadsAndVerifies()
    {
        byte[] payload = Encoding.UTF8.GetBytes("fake installer payload");
        string expectedHash = ComputeSha256(payload);

        var release = new ReleaseEntry(
            Version: "2.0.0",
            DownloadUrl: TestDownloadUrl,
            Sha256: expectedHash,
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow);

        using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, payload);
        using var httpClient = new HttpClient(handler);

        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.UpdateService.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new FakeAppStoragePaths(tempRoot);
        var service = new UpdateService(httpClient, new RecordingApplicationLogger(), storagePaths);

        try
        {
            UpdateDownloadResult result = await service.DownloadUpdateAsync(release);

            Assert.True(result.Success);
            Assert.NotNull(result.FilePath);
            Assert.True(File.Exists(result.FilePath));
            Assert.Equal(expectedHash, ComputeSha256(await File.ReadAllBytesAsync(result.FilePath)));
            Assert.Null(result.ErrorMessage);
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
    public async Task DownloadUpdateAsync_Sha256Mismatch_ReturnsCorruptError()
    {
        byte[] payload = Encoding.UTF8.GetBytes("fake installer payload");
        string wrongHash = "f".Repeat(64);

        var release = new ReleaseEntry(
            Version: "2.0.0",
            DownloadUrl: TestDownloadUrl,
            Sha256: wrongHash,
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow);

        using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, payload);
        using var httpClient = new HttpClient(handler);

        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.UpdateService.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new FakeAppStoragePaths(tempRoot);
        var service = new UpdateService(httpClient, new RecordingApplicationLogger(), storagePaths);

        try
        {
            UpdateDownloadResult result = await service.DownloadUpdateAsync(release);

            Assert.False(result.Success);
            Assert.Null(result.FilePath);
            Assert.NotNull(result.ErrorMessage);
            Assert.Contains("SHA-256 mismatch", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Contains("corrupt", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
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
    public async Task DownloadUpdateAsync_HttpFailure_ReturnsDownloadFailed()
    {
        var release = new ReleaseEntry(
            Version: "2.0.0",
            DownloadUrl: TestDownloadUrl,
            Sha256: "a".Repeat(64),
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow);

        using var handler = new StaticHttpMessageHandler(HttpStatusCode.NotFound, Array.Empty<byte>());
        using var httpClient = new HttpClient(handler);

        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.UpdateService.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new FakeAppStoragePaths(tempRoot);
        var service = new UpdateService(httpClient, new RecordingApplicationLogger(), storagePaths);

        try
        {
            UpdateDownloadResult result = await service.DownloadUpdateAsync(release);

            Assert.False(result.Success);
            Assert.Null(result.FilePath);
            Assert.NotNull(result.ErrorMessage);
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
    public async Task DownloadUpdateAsync_AlreadyDownloadedAndVerified_UsesCached()
    {
        byte[] payload = Encoding.UTF8.GetBytes("verified installer payload");
        string expectedHash = ComputeSha256(payload);

        var release = new ReleaseEntry(
            Version: "2.0.0",
            DownloadUrl: TestDownloadUrl,
            Sha256: expectedHash,
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow);

        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.UpdateService.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new FakeAppStoragePaths(tempRoot);

        try
        {
            string updatesDir = GetUpdatesDirectory(tempRoot);
            Directory.CreateDirectory(updatesDir);
            string cachedPath = Path.Combine(updatesDir, ResolveInstallerFileNameForPlatform("2.0.0"));
            await File.WriteAllBytesAsync(cachedPath, payload);

            using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, payload);
            using var httpClient = new HttpClient(handler);
            var service = new UpdateService(httpClient, new RecordingApplicationLogger(), storagePaths);

            UpdateDownloadResult result = await service.DownloadUpdateAsync(release);

            Assert.True(result.Success);
            Assert.Equal(cachedPath, result.FilePath);
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
    public async Task DownloadUpdateAsync_CachedFileHashMismatch_ReDownloads()
    {
        byte[] oldPayload = Encoding.UTF8.GetBytes("old corrupt payload");
        byte[] newPayload = Encoding.UTF8.GetBytes("new correct payload");
        string expectedHash = ComputeSha256(newPayload);

        var release = new ReleaseEntry(
            Version: "2.0.0",
            DownloadUrl: TestDownloadUrl,
            Sha256: expectedHash,
            ReleaseNotesUrl: null,
            PublishedAt: DateTimeOffset.UtcNow);

        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.UpdateService.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new FakeAppStoragePaths(tempRoot);

        try
        {
            string updatesDir = GetUpdatesDirectory(tempRoot);
            Directory.CreateDirectory(updatesDir);
            string cachedPath = Path.Combine(updatesDir, ResolveInstallerFileNameForPlatform("2.0.0"));
            await File.WriteAllBytesAsync(cachedPath, oldPayload);

            using var handler = new StaticHttpMessageHandler(HttpStatusCode.OK, newPayload);
            using var httpClient = new HttpClient(handler);
            var service = new UpdateService(httpClient, new RecordingApplicationLogger(), storagePaths);

            UpdateDownloadResult result = await service.DownloadUpdateAsync(release);

            Assert.True(result.Success);
            Assert.NotNull(result.FilePath);
            Assert.Equal(expectedHash, ComputeSha256(await File.ReadAllBytesAsync(result.FilePath)));
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
    public async Task LaunchInstallerAsync_FileNotFound_ReturnsFalse()
    {
        using var httpClient = new HttpClient(new StaticHttpMessageHandler(HttpStatusCode.OK, ""));
        var service = CreateService(httpClient);

        bool result = await service.LaunchInstallerAsync("C:\\nonexistent\\installer.exe");

        Assert.False(result);
    }

    private static UpdateService CreateService(HttpClient httpClient)
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.UpdateService.Tests", Guid.NewGuid().ToString("N"));
        var storagePaths = new FakeAppStoragePaths(tempRoot);
        return new UpdateService(httpClient, new RecordingApplicationLogger(), storagePaths);
    }

    private static string ComputeSha256(byte[] data)
    {
        byte[] hash = SHA256.HashData(data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private sealed class StaticHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode statusCode;
        private readonly byte[] payloadBytes;
        private readonly string? payloadString;

        public StaticHttpMessageHandler(HttpStatusCode statusCode, byte[] payload)
        {
            this.statusCode = statusCode;
            payloadBytes = payload;
        }

        public StaticHttpMessageHandler(HttpStatusCode statusCode, string payload)
        {
            this.statusCode = statusCode;
            payloadString = payload;
            payloadBytes = Encoding.UTF8.GetBytes(payload);
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(statusCode)
            {
                Content = payloadString is not null
                    ? new StringContent(payloadString, Encoding.UTF8, "application/json")
                    : new ByteArrayContent(payloadBytes),
                RequestMessage = request
            };

            response.Content.Headers.ContentLength = payloadBytes.Length;
            return Task.FromResult(response);
        }
    }

    private sealed class ThrowingHttpMessageHandler(HttpRequestException exception) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            throw exception;
        }
    }

    private sealed class CancellableHttpMessageHandler(CancellationTokenSource cts) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cts.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        }
    }

    private static string GetUpdatesDirectory(string cacheRoot) =>
        Path.Combine(cacheRoot, "Trackdub", "updates");

    private static string ResolveInstallerFileNameForPlatform(string version)
    {
        if (OperatingSystem.IsWindows())
        {
            return $"Trackdub-{version}-setup.exe";
        }

        if (OperatingSystem.IsMacOS())
        {
            return $"Trackdub-{version}.dmg";
        }

        return $"Trackdub-{version}-x86_64.AppImage";
    }

    private sealed class RecordingApplicationLogger : IApplicationLogger
    {
        public void LogDebug(string message) { }
        public void LogInformation(string message) { }
        public void LogWarning(string message, Exception? exception = null) { }
        public void LogError(string message, Exception? exception = null) { }
    }

    private sealed class FakeAppStoragePaths : IAppStoragePaths
    {
        public FakeAppStoragePaths(string root)
        {
            UserDataRoot = root;
            UserCacheRoot = root;
        }

        public string RootDirectory => UserDataRoot;
        public string UserDataRoot { get; }
        public string UserCacheRoot { get; }
        public string? SharedAssetRoot => null;
        public bool IsPortable => false;
        public string ModelCacheDirectory => Path.Combine(UserDataRoot, "model-cache");
        public string ModelCacheIndexPath => Path.Combine(ModelCacheDirectory, "model-cache-records.json");
        public string LogFilePath => Path.Combine(UserDataRoot, "trackdub.log");
        public string SettingsPath => Path.Combine(UserDataRoot, "settings.json");
        public string LayoutPath => Path.Combine(UserDataRoot, "avalonia-layout.json");
        public string ToolCacheDirectory => Path.Combine(UserCacheRoot, "tools");
        public string FfmpegToolCacheDirectory => Path.Combine(ToolCacheDirectory, "ffmpeg");
        public string EngineCacheDirectory => Path.Combine(UserCacheRoot, "EngineCache");
        public string ComponentCacheDirectory => Path.Combine(UserCacheRoot, "components");
    }
}

internal static class StringExtensions
{
    public static string Repeat(this string s, int count) =>
        string.Concat(Enumerable.Repeat(s, count));
}
