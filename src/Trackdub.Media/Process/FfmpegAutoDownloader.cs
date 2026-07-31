using System.Diagnostics;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace Trackdub.Media.Process;

/// <summary>
/// A resolved ffmpeg download target. <see cref="Sha256"/> is <c>null</c> when the
/// source is a mutable/rolling release (e.g. a "latest" tag that gets republished with
/// new assets over time) — there is no fixed content to publish a hash for ahead of time.
/// <see cref="FfmpegAutoDownloader"/> falls back to trust-on-first-use for those: the
/// first download's hash is persisted and compared against on any later re-download,
/// which doesn't authenticate the first download but does catch a rolling release
/// silently changing underneath an already-trusted install. A non-null value means the
/// source is an immutable, versioned release with a real hash checked every time.
/// </summary>
internal sealed record FfmpegDownloadPackage(
    string VersionTag,
    string AssetFileName,
    string DownloadUrl,
    string? Sha256);

internal sealed class FfmpegAutoDownloader : IFfmpegAutoDownloader
{
    private const string ToolsDirectoryName = "tools";
    private const string FfmpegDirectoryName = "ffmpeg";
    private const string ExtractedPayloadDirectoryName = "payload";
    private const string ToolCacheRootEnvironmentVariable = "TRACKDUB_TOOL_CACHE_ROOT";
    private const string CacheRootEnvironmentVariable = "TRACKDUB_CACHE_ROOT";
    private const int BufferSize = 65536;

    private static readonly Lock SyncRoot = new();
    private static readonly HttpClient SharedHttpClient = CreateHttpClient();

    // Windows x64: GyanD (gyan.dev), an immutable-per-version distributor (100+ retained
    // releases going back to 2025-10, no observed pruning, unlike BtbN's dated
    // autobuild-* tags — see #23). "essentials_build" is a real GPLv3 build with
    // libx264 present (verified directly: extracted the binary, ran `-encoders`, saw
    // libx264/libx264rgb; `-version` reports --enable-gpl --enable-version3
    // --enable-libx264 — see #24). Hash computed by downloading the exact asset and
    // running sha256sum; GyanD doesn't publish its own checksums.
    private static readonly FfmpegDownloadPackage DefaultX64Package = new(
        "gyan-8.1.2-win64-gpl-essentials",
        "ffmpeg-8.1.2-essentials_build.zip",
        "https://github.com/GyanD/codexffmpeg/releases/download/8.1.2/ffmpeg-8.1.2-essentials_build.zip",
        "db580001caa24ac104c8cb856cd113a87b0a443f7bdf47d8c12b1d740584a2ec");

    // GyanD doesn't publish arm64/Linux builds. BtbN's literal "latest" tag (not a dated
    // autobuild-* tag) is their continuously-republished current-pointer — confirmed live,
    // never observed to 404, unlike the dated tags this used to pin to. Sha256 is
    // intentionally null: there's no fixed asset to hash against a moving target (see
    // FfmpegDownloadPackage's doc comment). "gpl-shared" (not "lgpl-shared") so libx264
    // is present here too — see #24.
    private static readonly FfmpegDownloadPackage DefaultArm64Package = new(
        "btbn-latest-winarm64-gpl-shared",
        "ffmpeg-master-latest-winarm64-gpl-shared.zip",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-winarm64-gpl-shared.zip",
        null);

    private static readonly FfmpegDownloadPackage ExplicitLinuxX64Package = new(
        "explicit-ffmpeg-linux64-gpl-shared-latest",
        "ffmpeg-master-latest-linux64-gpl-shared.tar.xz",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linux64-gpl-shared.tar.xz",
        null);
    private static readonly FfmpegDownloadPackage ExplicitLinuxArm64Package = new(
        "explicit-ffmpeg-linuxarm64-gpl-shared-latest",
        "ffmpeg-master-latest-linuxarm64-gpl-shared.tar.xz",
        "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-linuxarm64-gpl-shared.tar.xz",
        null);

    internal static FfmpegDownloadPackage DefaultPackage => GetDefaultPackage(RuntimeInformation.OSArchitecture);

    internal static readonly FfmpegAutoDownloader Shared = new();

    private readonly string installRootBase;
    private readonly bool installRootBaseIsToolCacheRoot;
    private readonly HttpClient httpClient;
    private readonly FfmpegDownloadPackage package;
    private readonly bool packageWasSupplied;

    public FfmpegAutoDownloader(
        string? localAppDataRoot = null,
        HttpClient? httpClient = null,
        FfmpegDownloadPackage? package = null)
    {
        if (!string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            installRootBase = NormalizePath(localAppDataRoot);
            installRootBaseIsToolCacheRoot = false;
        }
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ToolCacheRootEnvironmentVariable)))
        {
            installRootBase = NormalizePath(Environment.GetEnvironmentVariable(ToolCacheRootEnvironmentVariable)!);
            installRootBaseIsToolCacheRoot = true;
        }
        else if (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(CacheRootEnvironmentVariable)))
        {
            installRootBase = Path.Combine(
                NormalizePath(Environment.GetEnvironmentVariable(CacheRootEnvironmentVariable)!),
                ToolsDirectoryName);
            installRootBaseIsToolCacheRoot = true;
        }
        else
        {
            string localAppDataRootFallback = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            installRootBase = string.IsNullOrWhiteSpace(localAppDataRootFallback)
                ? AppContext.BaseDirectory
                : localAppDataRootFallback;
            installRootBaseIsToolCacheRoot = false;
        }

        this.httpClient = httpClient ?? SharedHttpClient;
        this.package = package ?? DefaultPackage;
        packageWasSupplied = package is not null;
    }

    public string? TryEnsureExecutable(IReadOnlyList<string> fallbacks)
    {
        if (!OperatingSystem.IsWindows() && !packageWasSupplied)
        {
            return null;
        }

        string installRoot = GetInstallRoot();
        string payloadRoot = Path.Combine(installRoot, ExtractedPayloadDirectoryName);

        lock (SyncRoot)
        {
            string? existing = FindExecutable(payloadRoot, fallbacks);
            if (!string.IsNullOrWhiteSpace(existing))
            {
                return existing;
            }

            Directory.CreateDirectory(installRoot);

            string tempArchivePath = Path.Combine(installRoot, $"{package.AssetFileName}.{Guid.NewGuid():N}.tmp");
            string tempExtractDirectory = Path.Combine(installRoot, $"extract-{Guid.NewGuid():N}");

            try
            {
                DownloadArchive(tempArchivePath);
                VerifyArchiveHash(tempArchivePath);

                Directory.CreateDirectory(tempExtractDirectory);
                ZipFile.ExtractToDirectory(tempArchivePath, tempExtractDirectory);

                if (Directory.Exists(payloadRoot))
                {
                    Directory.Delete(payloadRoot, recursive: true);
                }

                Directory.Move(tempExtractDirectory, payloadRoot);

                string? downloaded = FindExecutable(payloadRoot, fallbacks);
                if (!string.IsNullOrWhiteSpace(downloaded))
                {
                    return downloaded;
                }

                throw new InvalidOperationException(
                    $"Downloaded FFmpeg package '{package.AssetFileName}', but could not locate {string.Join(" or ", fallbacks)} in '{payloadRoot}'.");
            }
            catch
            {
                DeleteDirectoryIfExists(tempExtractDirectory);
                throw;
            }
            finally
            {
                DeleteFileIfExists(tempArchivePath);
            }
        }
    }

    internal async Task<bool> InstallExplicitAsync(CancellationToken ct)
    {
        if (OperatingSystem.IsWindows())
        {
            string? result = TryEnsureExecutable(GetPlatformExecutableNames("ffmpeg"));
            return result is not null;
        }

        string installRoot = GetInstallRoot();
        string payloadRoot = Path.Combine(installRoot, ExtractedPayloadDirectoryName);

        if (FindExecutable(payloadRoot, GetPlatformExecutableNames("ffmpeg")) is not null &&
            FindExecutable(payloadRoot, GetPlatformExecutableNames("ffprobe")) is not null)
        {
            return true;
        }

        Directory.CreateDirectory(installRoot);

        if (OperatingSystem.IsLinux())
        {
            return await InstallExplicitOnLinuxAsync(installRoot, payloadRoot, ct).ConfigureAwait(false);
        }

        if (OperatingSystem.IsMacOS())
        {
            return await InstallExplicitOnMacOSAsync(installRoot, payloadRoot, ct).ConfigureAwait(false);
        }

        return false;
    }

    private async Task<bool> InstallExplicitOnLinuxAsync(string installRoot, string payloadRoot, CancellationToken ct)
    {
        FfmpegDownloadPackage linuxPackage = RuntimeInformation.OSArchitecture is Architecture.Arm64
            ? ExplicitLinuxArm64Package
            : ExplicitLinuxX64Package;

        string tempArchivePath = Path.Combine(installRoot, $"{linuxPackage.AssetFileName}.{Guid.NewGuid():N}.tmp");
        string tempExtractDirectory = Path.Combine(installRoot, $"extract-{Guid.NewGuid():N}");

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, linuxPackage.DownloadUrl);
            using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using Stream networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            using (var fileStream = new FileStream(tempArchivePath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize))
            {
                await networkStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
                await fileStream.FlushAsync(ct).ConfigureAwait(false);
            }

            // No-op today (linuxPackage.Sha256 is null — see FfmpegDownloadPackage's doc
            // comment, both Linux packages track BtbN's "latest" tag). Wired up so this
            // path isn't silently unverifiable forever if a future distributor supports
            // pinning, and so it matches the Windows path's behavior instead of diverging.
            VerifyArchiveHash(tempArchivePath, linuxPackage.Sha256);

            Directory.CreateDirectory(tempExtractDirectory);

            using var tar = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "tar",
                    ArgumentList = { "-xf", tempArchivePath, "-C", tempExtractDirectory },
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                }
            };
            tar.Start();
            await tar.WaitForExitAsync(ct).ConfigureAwait(false);

            if (tar.ExitCode != 0)
            {
                string stderr = await tar.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
                throw new InvalidOperationException(
                    $"tar extraction failed (exit {tar.ExitCode}): {stderr}");
            }

            if (Directory.Exists(payloadRoot))
            {
                Directory.Delete(payloadRoot, recursive: true);
            }

            Directory.Move(tempExtractDirectory, payloadRoot);

            SetUnixExecutableBits(payloadRoot);

            return FindExecutable(payloadRoot, GetPlatformExecutableNames("ffmpeg")) is not null;
        }
        catch (OperationCanceledException)
        {
            DeleteDirectoryIfExists(tempExtractDirectory);
            throw;
        }
        catch
        {
            DeleteDirectoryIfExists(tempExtractDirectory);
            return false;
        }
        finally
        {
            DeleteFileIfExists(tempArchivePath);
        }
    }

    private async Task<bool> InstallExplicitOnMacOSAsync(string installRoot, string payloadRoot, CancellationToken ct)
    {
        string tempDir = Path.Combine(installRoot, $"extract-{Guid.NewGuid():N}");
        string ffmpegZipPath = Path.Combine(installRoot, $"ffmpeg-zip.{Guid.NewGuid():N}.tmp");
        string ffprobeZipPath = Path.Combine(installRoot, $"ffprobe-zip.{Guid.NewGuid():N}.tmp");

        try
        {
            Directory.CreateDirectory(tempDir);

            await DownloadToFileAsync("https://evermeet.cx/ffmpeg/getrelease/zip", ffmpegZipPath, ct).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(ffmpegZipPath, tempDir);

            await DownloadToFileAsync("https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip", ffprobeZipPath, ct).ConfigureAwait(false);
            ZipFile.ExtractToDirectory(ffprobeZipPath, tempDir);

            if (Directory.Exists(payloadRoot))
            {
                Directory.Delete(payloadRoot, recursive: true);
            }

            Directory.Move(tempDir, payloadRoot);

            SetUnixExecutableBits(payloadRoot);

            return FindExecutable(payloadRoot, GetPlatformExecutableNames("ffmpeg")) is not null;
        }
        catch (OperationCanceledException)
        {
            DeleteDirectoryIfExists(tempDir);
            throw;
        }
        catch
        {
            DeleteDirectoryIfExists(tempDir);
            return false;
        }
        finally
        {
            DeleteFileIfExists(ffmpegZipPath);
            DeleteFileIfExists(ffprobeZipPath);
        }
    }

    private async Task DownloadToFileAsync(string url, string destinationPath, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        using HttpResponseMessage response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        using Stream networkStream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        await networkStream.CopyToAsync(fileStream, ct).ConfigureAwait(false);
        await fileStream.FlushAsync(ct).ConfigureAwait(false);
    }

    string? IFfmpegAutoDownloader.TryResolveInstallRoot()
    {
        string payloadRoot = Path.Combine(GetInstallRoot(), ExtractedPayloadDirectoryName);
        return Directory.Exists(payloadRoot) ? payloadRoot : null;
    }

    internal string GetInstallRoot() =>
        installRootBaseIsToolCacheRoot
            ? Path.Combine(installRootBase, FfmpegDirectoryName, package.VersionTag)
            : Path.Combine(installRootBase, "Trackdub", ToolsDirectoryName, FfmpegDirectoryName, package.VersionTag);

    private void DownloadArchive(string destinationPath)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, package.DownloadUrl);
        using HttpResponseMessage response = httpClient.Send(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        using Stream networkStream = response.Content.ReadAsStream();
        using var fileStream = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        networkStream.CopyTo(fileStream);
    }

    private void VerifyArchiveHash(string archivePath) => VerifyArchiveHash(archivePath, package.Sha256);

    /// <summary>
    /// Verifies <paramref name="archivePath"/> against <paramref name="expectedSha256"/>.
    /// Rolling releases use TOFU: the first downloaded hash is persisted in the package
    /// cache and every subsequent download must match it. This does not claim to provide
    /// authenticity on first use, but prevents a mutable release from silently changing
    /// underneath an existing installation.
    /// </summary>
    private void VerifyArchiveHash(string archivePath, string? expectedSha256)
    {
        using var stream = new FileStream(archivePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize);
        string hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();

        if (expectedSha256 is not null)
        {
            if (!string.Equals(hash, expectedSha256, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"FFmpeg download hash mismatch. Expected {expectedSha256}, got {hash}.");
            }

            return;
        }

        string hashPath = Path.Combine(GetInstallRoot(), "archive.sha256");
        if (File.Exists(hashPath))
        {
            string trustedHash = File.ReadAllText(hashPath).Trim();
            if (!string.Equals(hash, trustedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"FFmpeg rolling-release hash changed. Expected {trustedHash}, got {hash}.");
            }

            return;
        }

        Console.Error.WriteLine($"FFmpeg package '{package.AssetFileName}' has no published hash; recording first-seen hash (TOFU): {hash}.");
        string temporaryHashPath = $"{hashPath}.{Guid.NewGuid():N}.tmp";
        File.WriteAllText(temporaryHashPath, hash + Environment.NewLine);
        File.Move(temporaryHashPath, hashPath);
    }

    internal static string? FindExecutable(string root, IReadOnlyList<string> fallbacks)
    {
        if (!Directory.Exists(root))
        {
            return null;
        }

        foreach (string fallback in fallbacks)
        {
            try
            {
                string? candidate = Directory.EnumerateFiles(root, fallback, SearchOption.AllDirectories).FirstOrDefault();
                if (!string.IsNullOrWhiteSpace(candidate))
                {
                    return candidate;
                }
            }
            catch (UnauthorizedAccessException)
            {
            }
            catch (DirectoryNotFoundException)
            {
            }
            catch (IOException)
            {
            }
        }

        return null;
    }

    private static void SetUnixExecutableBits(string payloadRoot)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsMacOS())
        {
            return;
        }

        try
        {
            foreach (string file in Directory.EnumerateFiles(payloadRoot, "*", SearchOption.AllDirectories))
            {
                string fileName = Path.GetFileName(file);
                if (string.Equals(fileName, "ffmpeg", StringComparison.Ordinal) ||
                    string.Equals(fileName, "ffprobe", StringComparison.Ordinal))
                {
                    try
                    {
                        File.SetUnixFileMode(file,
                            UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute |
                            UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
                    }
                    catch (PlatformNotSupportedException) { }
                    catch (UnauthorizedAccessException) { }
                    catch (IOException) { }
                }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    private static HttpClient CreateHttpClient()
    {
        HttpClient client = new();
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Trackdub/1.0");
        return client;
    }

    internal static FfmpegDownloadPackage GetDefaultPackage(Architecture architecture) =>
        architecture is Architecture.Arm64
            ? DefaultArm64Package
            : DefaultX64Package;

    private static string NormalizePath(string path) =>
        Path.GetFullPath(Environment.ExpandEnvironmentVariables(path));

    internal static IReadOnlyList<string> GetPlatformExecutableNames(string baseName) =>
        OperatingSystem.IsWindows()
            ? [$"{baseName}.exe", baseName]
            : [baseName];

    private static void DeleteFileIfExists(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
