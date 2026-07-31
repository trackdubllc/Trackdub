using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Application.Updates;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;

namespace Trackdub.Infrastructure.Updates;

public sealed class UpdateService : Trackdub.Application.Updates.IUpdateService, IDisposable
{
    private const string DefaultReleaseManifestUrl = "https://releases.trackdub.ai/manifest.json";
    private static readonly string UpdateTempSubDir = Path.Combine("Trackdub", "updates");
    private const int BufferSize = 65536;

    private readonly HttpClient httpClient;
    private readonly IApplicationLogger logger;
    private readonly IAppStoragePaths storagePaths;
    private readonly bool ownsHttpClient;

    private bool disposed;

    public UpdateService(
        HttpClient httpClient,
        IApplicationLogger logger,
        IAppStoragePaths storagePaths)
    {
        this.httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
        this.storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        ownsHttpClient = false;
    }

    public async Task<Trackdub.Application.Updates.UpdateCheckResult> CheckForUpdateAsync(
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        try
        {
            string json = await httpClient
                .GetStringAsync(DefaultReleaseManifestUrl, cancellationToken)
                .ConfigureAwait(false);

            var schema = JsonSerializer.Deserialize<ReleaseManifestSchema>(json);
            if (schema is null || string.IsNullOrWhiteSpace(schema.LatestVersion))
            {
                return new Trackdub.Application.Updates.UpdateCheckResult(false, null, "Release manifest could not be parsed.");
            }

            if (!Version.TryParse(schema.LatestVersion, out Version? latest) ||
                !Version.TryParse(currentVersion, out Version? current))
            {
                return new Trackdub.Application.Updates.UpdateCheckResult(false, null,
                    "Version format in manifest is invalid.");
            }

            if (latest <= current)
            {
                logger.LogInformation(
                    $"No update available (current={currentVersion}, latest={schema.LatestVersion}).");
                return new Trackdub.Application.Updates.UpdateCheckResult(false, null, null);
            }

            if (!Uri.TryCreate(schema.DownloadUrl, UriKind.Absolute, out Uri? downloadUri))
            {
                return new Trackdub.Application.Updates.UpdateCheckResult(false, null,
                    "Download URL in release manifest is invalid.");
            }

            var release = new ReleaseEntry(
                schema.LatestVersion,
                downloadUri,
                schema.Sha256,
                schema.ReleaseNotesUrl,
                schema.PublishedAt);

            logger.LogInformation(
                $"Update available: {schema.LatestVersion} (current={currentVersion}).");

            return new Trackdub.Application.Updates.UpdateCheckResult(true, release, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new Trackdub.Application.Updates.UpdateCheckResult(false, null, "Update check was cancelled.");
        }
        catch (HttpRequestException ex)
        {
            logger.LogWarning("Update check failed (network error).", ex);
            return new Trackdub.Application.Updates.UpdateCheckResult(false, null,
                $"Could not reach update server: {ex.Message}");
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning("Update check timed out.", ex);
            return new Trackdub.Application.Updates.UpdateCheckResult(false, null,
                "Update check timed out. Please check your connection.");
        }
        catch (Exception ex)
        {
            logger.LogError("Update check failed unexpectedly.", ex);
            return new Trackdub.Application.Updates.UpdateCheckResult(false, null,
                $"Update check failed: {ex.Message}");
        }
    }

    public async Task<UpdateDownloadResult> DownloadUpdateAsync(
        ReleaseEntry release,
        IProgress<ModelDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        ArgumentNullException.ThrowIfNull(release);

        string tempDir = GetUpdateTempDirectory();
        Directory.CreateDirectory(tempDir);

        string installerFileName = ResolveInstallerFileName(release.Version);
        string tempPath = Path.Combine(tempDir, installerFileName);
        string tempPartialPath = tempPath + ".partial";

        try
        {
            logger.LogInformation(
                $"Downloading update {release.Version} from {release.DownloadUrl}");

            if (File.Exists(tempPath))
            {
                bool hashOk = await VerifySha256Async(tempPath, release.Sha256, cancellationToken)
                    .ConfigureAwait(false);
                if (hashOk)
                {
                    logger.LogInformation(
                        $"Update package already downloaded and verified at {tempPath}");
                    return new UpdateDownloadResult(true, tempPath, null);
                }

                logger.LogInformation(
                    "Cached update package hash mismatch; re-downloading.");
                TryDelete(tempPath);
            }

            bool downloaded = await DownloadFileAsync(
                release.DownloadUrl, tempPartialPath, progress, cancellationToken)
                .ConfigureAwait(false);

            if (!downloaded)
            {
                return new UpdateDownloadResult(false, null, "Download failed.");
            }

            if (cancellationToken.IsCancellationRequested)
            {
                return new UpdateDownloadResult(false, null, "Download was cancelled.");
            }

            File.Move(tempPartialPath, tempPath, overwrite: true);

            bool hashValid = await VerifySha256Async(tempPath, release.Sha256, cancellationToken)
                .ConfigureAwait(false);

            if (!hashValid)
            {
                TryDelete(tempPath);
                logger.LogError(
                    $"SHA-256 mismatch for downloaded update package {tempPath}.");
                return new UpdateDownloadResult(false, null,
                    "Download may be corrupt: SHA-256 mismatch. Please try again.");
            }

            logger.LogInformation(
                $"Update package downloaded and verified: {tempPath}");
            return new UpdateDownloadResult(true, tempPath, null);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new UpdateDownloadResult(false, null, "Download was cancelled.");
        }
        catch (Exception ex)
        {
            logger.LogError("Update download failed.", ex);
            TryDelete(tempPartialPath);
            return new UpdateDownloadResult(false, null,
                $"Download failed: {ex.Message}");
        }
    }

    public Task<bool> LaunchInstallerAsync(
        string installerPath,
        CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();

        if (string.IsNullOrWhiteSpace(installerPath))
        {
            throw new ArgumentException("Installer path must not be empty.", nameof(installerPath));
        }

        if (!File.Exists(installerPath))
        {
            logger.LogError($"Installer not found at {installerPath}");
            return Task.FromResult(false);
        }

        try
        {
            string fullPath = Path.GetFullPath(installerPath);

            using Process process = new();
            process.StartInfo.FileName = fullPath;
            process.StartInfo.UseShellExecute = true;

            if (OperatingSystem.IsWindows())
            {
                process.StartInfo.Verb = "runas";
            }

            bool started = process.Start();
            if (!started)
            {
                logger.LogError($"Failed to start installer process: {installerPath}");
                return Task.FromResult(false);
            }

            logger.LogInformation(
                $"Installer launched (pid={process.Id}): {installerPath}");
            return Task.FromResult(true);
        }
        catch (Exception ex)
        {
            logger.LogError("Failed to launch installer.", ex);
            return Task.FromResult(false);
        }
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

    private async Task<bool> DownloadFileAsync(
        Uri sourceUri,
        string destinationPath,
        IProgress<ModelDownloadProgress>? progress,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await httpClient
                .GetAsync(sourceUri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                logger.LogError(
                    $"Update download failed with status {response.StatusCode}: {sourceUri}");
                return false;
            }

            long? contentLength = response.Content.Headers.ContentLength;
            long totalBytesRead = 0;
            var stopwatch = Stopwatch.StartNew();
            var lastReportTime = stopwatch.Elapsed;
            long sessionBytesRead = 0;

            string? directory = Path.GetDirectoryName(destinationPath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            await using Stream contentStream = await response.Content
                .ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);

            await using var fileStream = new FileStream(
                destinationPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                BufferSize,
                useAsync: true);

            byte[] buffer = new byte[BufferSize];
            int bytesRead;

            while ((bytesRead = await contentStream
                .ReadAsync(buffer, cancellationToken)
                .ConfigureAwait(false)) != 0)
            {
                await fileStream
                    .WriteAsync(buffer, 0, bytesRead, cancellationToken)
                    .ConfigureAwait(false);

                totalBytesRead += bytesRead;
                sessionBytesRead += bytesRead;

                var now = stopwatch.Elapsed;
                if (now - lastReportTime >= TimeSpan.FromMilliseconds(250) ||
                    totalBytesRead == contentLength)
                {
                    int percentComplete = contentLength is > 0 and var total
                        ? (int)((totalBytesRead * 100) / total)
                        : 0;

                    double elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
                    double speed = elapsedSeconds > 0 ? sessionBytesRead / elapsedSeconds : 0;

                    TimeSpan? eta = null;
                    if (contentLength is > 0 and var totalForEta && speed > 0)
                    {
                        long remainingBytes = totalForEta - totalBytesRead;
                        eta = TimeSpan.FromSeconds(remainingBytes / speed);
                    }

                    progress?.Report(new ModelDownloadProgress(
                        totalBytesRead,
                        contentLength,
                        percentComplete,
                        $"Downloading update ({FormatBytes(totalBytesRead)} of {(contentLength is { } c ? FormatBytes(c) : "unknown")})",
                        speed,
                        eta));

                    lastReportTime = now;
                }
            }

            logger.LogInformation(
                $"Update download completed: {destinationPath} ({FormatBytes(totalBytesRead)})");
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError($"Update download stream failed.", ex);
            return false;
        }
    }

    private static async Task<bool> VerifySha256Async(
        string filePath,
        string expectedHash,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }

        try
        {
            await using var stream = new FileStream(
                filePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                BufferSize,
                useAsync: true);

            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken)
                .ConfigureAwait(false);

            string actualHash = Convert.ToHexString(hash).ToLowerInvariant();

            return string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private string GetUpdateTempDirectory()
    {
        string baseDir = !string.IsNullOrWhiteSpace(storagePaths.UserCacheRoot)
            ? storagePaths.UserCacheRoot
            : Path.GetTempPath();

        return Path.Combine(baseDir, "Trackdub", "updates");
    }

    private static string ResolveInstallerFileName(string version)
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
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

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(disposed, this);
    }
}
