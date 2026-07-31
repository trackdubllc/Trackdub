using System.IO.Compression;
using System.Net;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using Trackdub.Media.Process;

namespace Trackdub.Media.Tests;

public sealed class FfmpegAutoDownloaderTests
{
    [Fact]
    public void Shared_uses_default_package_when_computing_install_root()
    {
        string installRoot = FfmpegAutoDownloader.Shared.GetInstallRoot();

        Assert.EndsWith(
            Path.Combine("Trackdub", "tools", "ffmpeg", FfmpegAutoDownloader.DefaultPackage.VersionTag),
            installRoot,
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void GetDefaultPackage_uses_architecture_specific_windows_assets()
    {
        FfmpegDownloadPackage x64 = FfmpegAutoDownloader.GetDefaultPackage(Architecture.X64);
        FfmpegDownloadPackage arm64 = FfmpegAutoDownloader.GetDefaultPackage(Architecture.Arm64);

        // x64 comes from GyanD, an immutable versioned release — pinned and hash-verified.
        // (GyanD's own naming doesn't include "win64" — it's a Windows-only distributor,
        // so its filenames don't need an arch qualifier; the URL host is what identifies it.)
        Assert.Contains("GyanD", x64.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("arm64", x64.AssetFileName, StringComparison.OrdinalIgnoreCase);
        Assert.NotNull(x64.Sha256);
        Assert.Equal(64, x64.Sha256!.Length);
        // GyanD doesn't embed the license variant in its filename the way BtbN does
        // (it only ever publishes GPL builds) — VersionTag records it for clarity.
        Assert.Contains("gpl", x64.VersionTag, StringComparison.OrdinalIgnoreCase);

        // arm64 comes from BtbN's rolling "latest" tag — no fixed content to pin a hash
        // against (see FfmpegDownloadPackage's doc comment), so Sha256 is deliberately null.
        Assert.Contains("winarm64", arm64.AssetFileName, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("winarm64", arm64.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/latest/", arm64.DownloadUrl, StringComparison.OrdinalIgnoreCase);
        Assert.Null(arm64.Sha256);
        Assert.Contains("gpl", arm64.AssetFileName, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("lgpl", arm64.AssetFileName, StringComparison.OrdinalIgnoreCase);

        Assert.NotEqual(x64.VersionTag, arm64.VersionTag);
    }

    [Fact]
    public void TryEnsureExecutable_records_tofu_baseline_when_package_has_no_pinned_hash()
    {
        // Mirrors the real arm64/Linux packages: Sha256 is null because the source is a
        // mutable "latest" tag. First install has no prior baseline to compare against,
        // so it must succeed and record one — not silently skip verification forever.
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            byte[] archiveBytes = CreateArchiveBytes();
            var package = new FfmpegDownloadPackage(
                "test-build-unverified",
                "ffmpeg-test.zip",
                "https://example.invalid/ffmpeg-test.zip",
                Sha256: null);

            using var client = new HttpClient(new StaticArchiveHandler(archiveBytes));
            var downloader = new FfmpegAutoDownloader(tempRoot, client, package);

            string? ffmpegPath = downloader.TryEnsureExecutable(["ffmpeg.exe"]);

            Assert.NotNull(ffmpegPath);
            Assert.True(File.Exists(ffmpegPath));

            string hashPath = Path.Combine(downloader.GetInstallRoot(), "archive.sha256");
            Assert.True(File.Exists(hashPath));
            string expectedHash = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            Assert.Equal(expectedHash, File.ReadAllText(hashPath).Trim(), ignoreCase: true);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void TryEnsureExecutable_rejects_reinstall_when_rolling_release_content_changed()
    {
        // Simulates a second install attempt (e.g. after the extracted payload was
        // deleted but the TOFU baseline survived) where the "latest" tag has since been
        // republished with different bytes — must fail loudly, not silently re-trust.
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var package = new FfmpegDownloadPackage(
                "test-build-unverified",
                "ffmpeg-test.zip",
                "https://example.invalid/ffmpeg-test.zip",
                Sha256: null);

            using (var firstClient = new HttpClient(new StaticArchiveHandler(CreateArchiveBytes())))
            {
                var firstDownloader = new FfmpegAutoDownloader(tempRoot, firstClient, package);
                Assert.NotNull(firstDownloader.TryEnsureExecutable(["ffmpeg.exe"]));
            }

            string installRoot = new FfmpegAutoDownloader(tempRoot, package: package).GetInstallRoot();
            Directory.Delete(Path.Combine(installRoot, "payload"), recursive: true);

            byte[] differentArchiveBytes = CreateArchiveBytes("ffmpeg-v2", "ffprobe-v2");
            using var secondClient = new HttpClient(new StaticArchiveHandler(differentArchiveBytes));
            var secondDownloader = new FfmpegAutoDownloader(tempRoot, secondClient, package);

            var ex = Assert.Throws<InvalidOperationException>(() => secondDownloader.TryEnsureExecutable(["ffmpeg.exe"]));
            Assert.Contains("rolling-release hash changed", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    [Fact]
    public void Constructor_uses_tool_cache_environment_root_when_local_root_is_not_supplied()
    {
        _ = FfmpegAutoDownloader.Shared; // ensure type initializer runs before env vars are mutated
        string? previousToolCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_TOOL_CACHE_ROOT");
        string? previousCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_CACHE_ROOT");
        string toolCacheRoot = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Media.Tests",
            Guid.NewGuid().ToString("N"),
            "tools");
        var package = new FfmpegDownloadPackage(
            "test-build",
            "ffmpeg-test.zip",
            "https://example.invalid/ffmpeg-test.zip",
            "abc123");

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_TOOL_CACHE_ROOT", toolCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", null);

            var downloader = new FfmpegAutoDownloader(package: package);

            Assert.Equal(
                Path.Combine(Path.GetFullPath(toolCacheRoot), "ffmpeg", "test-build"),
                downloader.GetInstallRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_TOOL_CACHE_ROOT", previousToolCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", previousCacheRoot);
        }
    }

    [Fact]
    public void Constructor_uses_cache_environment_root_when_tool_cache_root_is_not_supplied()
    {
        _ = FfmpegAutoDownloader.Shared; // ensure type initializer runs before env vars are mutated
        string? previousToolCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_TOOL_CACHE_ROOT");
        string? previousCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_CACHE_ROOT");
        string cacheRoot = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Media.Tests",
            Guid.NewGuid().ToString("N"),
            "cache");
        var package = new FfmpegDownloadPackage(
            "test-build",
            "ffmpeg-test.zip",
            "https://example.invalid/ffmpeg-test.zip",
            "abc123");

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_TOOL_CACHE_ROOT", null);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", cacheRoot);

            var downloader = new FfmpegAutoDownloader(package: package);

            Assert.Equal(
                Path.Combine(Path.GetFullPath(cacheRoot), "tools", "ffmpeg", "test-build"),
                downloader.GetInstallRoot());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_TOOL_CACHE_ROOT", previousToolCacheRoot);
            Environment.SetEnvironmentVariable("TRACKDUB_CACHE_ROOT", previousCacheRoot);
        }
    }

    [Fact]
    public void TryEnsureExecutable_downloads_and_extracts_pinned_ffmpeg_package()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Media.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            byte[] archiveBytes = CreateArchiveBytes();
            string sha256 = Convert.ToHexString(SHA256.HashData(archiveBytes)).ToLowerInvariant();
            var package = new FfmpegDownloadPackage(
                "test-build",
                "ffmpeg-test.zip",
                "https://example.invalid/ffmpeg-test.zip",
                sha256);

            using var client = new HttpClient(new StaticArchiveHandler(archiveBytes));
            var downloader = new FfmpegAutoDownloader(tempRoot, client, package);

            string? ffmpegPath = downloader.TryEnsureExecutable(["ffmpeg.exe"]);

            Assert.NotNull(ffmpegPath);
            Assert.True(File.Exists(ffmpegPath));
            Assert.Contains(Path.Combine("Trackdub", "tools", "ffmpeg", "test-build"), ffmpegPath, StringComparison.OrdinalIgnoreCase);
            Assert.True(File.Exists(Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe")));
        }
        finally
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }

    private static byte[] CreateArchiveBytes(string ffmpegContent = "ffmpeg", string ffprobeContent = "ffprobe")
    {
        using var stream = new MemoryStream();
        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            AddEntry(archive, "ffmpeg-master-latest-win64-lgpl-shared/bin/ffmpeg.exe", ffmpegContent);
            AddEntry(archive, "ffmpeg-master-latest-win64-lgpl-shared/bin/ffprobe.exe", ffprobeContent);
            AddEntry(archive, "ffmpeg-master-latest-win64-lgpl-shared/LICENSE.txt", "LGPL");
        }

        return stream.ToArray();
    }

    private static void AddEntry(ZipArchive archive, string path, string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(path);
        using StreamWriter writer = new(entry.Open());
        writer.Write(content);
    }

    private sealed class StaticArchiveHandler(byte[] archiveBytes) : HttpMessageHandler
    {
        protected override HttpResponseMessage Send(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(archiveBytes)
            };
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            return Task.FromResult(Send(request, cancellationToken));
        }
    }
}
