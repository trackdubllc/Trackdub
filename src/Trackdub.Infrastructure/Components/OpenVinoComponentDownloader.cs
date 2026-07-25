using System.IO.Compression;
using System.Security.Cryptography;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Components;

/// <summary>
/// Handles download, integrity verification, extraction, and uninstall of the OpenVINO
/// component package. Reports progress via <see cref="IProgress{T}"/>. Supports cancellation.
/// </summary>
public sealed class OpenVinoComponentDownloader
{
    /// <summary>
    /// The component identifier used in the <see cref="ComponentStore"/>.
    /// </summary>
    public const string ComponentId = "openvino";

    private const int BufferSize = 65536; // 64 KB chunks
    private const string TempFileSuffix = ".downloading";
    private const string StagingDirectorySuffix = ".staging";

    private readonly ComponentStore _componentStore;
    private readonly HttpClient _httpClient;
    private readonly IApplicationLogger _logger;
    private readonly OpenVinoComponentSettings _settings;
    private readonly bool _allowInsecureComponentDownload;

    public OpenVinoComponentDownloader(
        ComponentStore componentStore,
        HttpClient httpClient,
        IApplicationLogger logger,
        OpenVinoComponentSettings settings,
        bool allowInsecureComponentDownload)
    {
        ArgumentNullException.ThrowIfNull(componentStore);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(settings);

        _componentStore = componentStore;
        _httpClient = httpClient;
        _logger = logger;
        _settings = settings;
        _allowInsecureComponentDownload = allowInsecureComponentDownload;
    }

    /// <summary>
    /// Downloads, verifies, and installs the OpenVINO component package.
    /// </summary>
    /// <param name="progress">Optional progress reporter (0.0 to 1.0).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>The install path on success.</returns>
    /// <exception cref="OpenVinoComponentException">Thrown when download, verification, or extraction fails.</exception>
    /// <exception cref="OperationCanceledException">Thrown when the operation is cancelled.</exception>
    public async Task<string> DownloadAndInstallAsync(
        IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        bool hasSha256 = !string.IsNullOrWhiteSpace(_settings.ExpectedSha256Hash);
        bool hasFileSize = _settings.ExpectedFileSizeBytes.HasValue;

        if (!hasSha256 && !hasFileSize)
        {
            throw new OpenVinoComponentException(
                $"OpenVINO download integrity metadata is required for component '{ComponentId}' from '{_settings.DownloadUrl}'. " +
                "Configure ExpectedSha256Hash or ExpectedFileSizeBytes before downloading.");
        }

        if (hasSha256 ^ hasFileSize)
        {
            string overrideContext = _allowInsecureComponentDownload
                ? " AllowInsecureComponentDownload is enabled but does not bypass integrity metadata requirements."
                : string.Empty;
            _logger.LogWarning(
                $"OpenVINO integrity verification for component '{ComponentId}' from '{_settings.DownloadUrl}' is using only a single metadata source " +
                $"(hash configured: {hasSha256}, file size configured: {hasFileSize}). Configure both hash and file size for stronger verification.{overrideContext}");
        }

        string componentDir = _componentStore.GetComponentDirectory(ComponentId);
        string tempFilePath = Path.Combine(componentDir, $"{ComponentId}{TempFileSuffix}");

        try
        {
            Directory.CreateDirectory(componentDir);

            _logger.LogInformation($"Starting OpenVINO component download from {_settings.DownloadUrl}");

            // Phase 1: Download
            await DownloadPackageAsync(tempFilePath, progress, cancellationToken).ConfigureAwait(false);

            // Phase 2: Verify integrity
            await VerifyIntegrityAsync(tempFilePath, cancellationToken).ConfigureAwait(false);

            // Phase 3: Extract
            string installPath = await ExtractPackageAsync(tempFilePath, componentDir, cancellationToken).ConfigureAwait(false);
            _componentStore.MarkInstalled(ComponentId);

            // Clean up temp file after successful extraction
            DeleteFileIfExists(tempFilePath);

            _logger.LogInformation($"OpenVINO component installed successfully at {installPath}");
            progress?.Report(1.0);

            return installPath;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("OpenVINO component download was cancelled.");
            CleanupPartialFiles(tempFilePath, componentDir);
            throw;
        }
        catch (OpenVinoComponentException)
        {
            CleanupPartialFiles(tempFilePath, componentDir);
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError($"OpenVINO component download failed for component '{ComponentId}' from '{_settings.DownloadUrl}'.", ex);
            CleanupPartialFiles(tempFilePath, componentDir);
            throw new OpenVinoComponentException(
                $"OpenVINO component download failed for component '{ComponentId}' from '{_settings.DownloadUrl}'.",
                ex);
        }
    }

    /// <summary>
    /// Uninstalls the OpenVINO component. If sessions are active (determined by
    /// <paramref name="hasActiveSessions"/>), defers removal and returns false.
    /// </summary>
    /// <param name="hasActiveSessions">
    /// A function that returns true if there are active ONNX sessions using the OpenVINO EP.
    /// </param>
    /// <returns>True if uninstall completed; false if deferred due to active sessions.</returns>
    public bool Uninstall(Func<bool>? hasActiveSessions = null)
    {
        if (!_componentStore.IsInstalled(ComponentId))
        {
            _logger.LogInformation("OpenVINO component is not installed; nothing to uninstall.");
            return true;
        }

        if (hasActiveSessions?.Invoke() == true)
        {
            _logger.LogWarning(
                "OpenVINO component uninstall deferred: active sessions are using the OpenVINO EP. " +
                "Removal will take effect after the current pipeline run completes.");
            return false;
        }

        // Best-effort guard: unloading can lag behind session completion on some hosts.
        // Probe quickly for file-lock behavior before deleting the install folder.
        string componentPath = _componentStore.GetComponentDirectory(ComponentId);
        if (Directory.Exists(componentPath) && IsComponentDirectoryLikelyInUse(componentPath))
        {
            _logger.LogWarning(
                "OpenVINO component uninstall deferred: component files still appear to be in use. " +
                "Retry uninstall after pipeline/runtime teardown.");
            return false;
        }

        _componentStore.Remove(ComponentId);
        _logger.LogInformation("OpenVINO component uninstalled successfully.");
        return true;
    }

    /// <summary>
    /// Asynchronous uninstall that waits for active sessions to complete before removing.
    /// </summary>
    /// <param name="hasActiveSessions">
    /// A function that returns true if there are active ONNX sessions using the OpenVINO EP.
    /// </param>
    /// <param name="cancellationToken">Cancellation token.</param>
    public async Task UninstallAsync(
        Func<bool>? hasActiveSessions = null,
        CancellationToken cancellationToken = default)
    {
        if (!_componentStore.IsInstalled(ComponentId))
        {
            _logger.LogInformation("OpenVINO component is not installed; nothing to uninstall.");
            return;
        }

        // Wait for active sessions to complete
        while (hasActiveSessions?.Invoke() == true)
        {
            _logger.LogInformation("Waiting for active OpenVINO sessions to complete before uninstall...");
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
        }

        string componentPath = _componentStore.GetComponentDirectory(ComponentId);
        while (Directory.Exists(componentPath) && IsComponentDirectoryLikelyInUse(componentPath))
        {
            _logger.LogInformation("Waiting for OpenVINO native files to be released before uninstall...");
            await Task.Delay(TimeSpan.FromSeconds(1), cancellationToken).ConfigureAwait(false);
        }

        _componentStore.Remove(ComponentId);
        _logger.LogInformation("OpenVINO component uninstalled successfully.");
    }

    private async Task DownloadPackageAsync(
        string tempFilePath,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, _settings.DownloadUrl);
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new OpenVinoComponentException(
                $"Download failed for component '{ComponentId}' from '{_settings.DownloadUrl}' " +
                $"with HTTP status {(int)response.StatusCode} ({response.StatusCode}).");
        }

        long? totalBytes = response.Content.Headers.ContentLength;
        long bytesRead = 0;

        await using var contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var fileStream = new FileStream(
            tempFilePath, FileMode.Create, FileAccess.Write, FileShare.None, BufferSize, useAsync: true);

        byte[] buffer = new byte[BufferSize];
        int read;
        var lastProgressReport = DateTimeOffset.MinValue;

        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            bytesRead += read;

            // Report progress at most every 500ms to avoid flooding the UI
            var now = DateTimeOffset.UtcNow;
            if (totalBytes > 0 && now - lastProgressReport >= TimeSpan.FromMilliseconds(500))
            {
                double fraction = (double)bytesRead / totalBytes.Value;
                // Download is 0.0–0.9 of total progress; verification+extraction is 0.9–1.0
                progress?.Report(fraction * 0.9);
                lastProgressReport = now;
            }
        }

        _logger.LogInformation($"OpenVINO package downloaded: {bytesRead} bytes.");
    }

    private async Task VerifyIntegrityAsync(string filePath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_settings.ExpectedSha256Hash))
        {
            // If no hash is configured, verify by file size only
            if (_settings.ExpectedFileSizeBytes.HasValue)
            {
                var fileInfo = new FileInfo(filePath);
                if (fileInfo.Length != _settings.ExpectedFileSizeBytes.Value)
                {
                    throw new OpenVinoComponentException(
                        $"Integrity verification failed: expected file size {_settings.ExpectedFileSizeBytes.Value} bytes, " +
                        $"got {fileInfo.Length} bytes. The download may be corrupted.");
                }
            }

            _logger.LogInformation("OpenVINO package integrity verified (size check).");
            return;
        }

        // SHA-256 hash verification
        await using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, BufferSize, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        string actualHash = Convert.ToHexString(hash).ToLowerInvariant();

        if (!string.Equals(actualHash, _settings.ExpectedSha256Hash, StringComparison.OrdinalIgnoreCase))
        {
            throw new OpenVinoComponentException(
                $"Integrity verification failed: SHA-256 hash mismatch. " +
                $"Expected '{_settings.ExpectedSha256Hash}', got '{actualHash}'. The download may be corrupted.");
        }

        _logger.LogInformation("OpenVINO package integrity verified (SHA-256).");
    }

    private async Task<string> ExtractPackageAsync(
        string archivePath,
        string componentDir,
        CancellationToken cancellationToken)
    {
        string parentDir = Path.GetDirectoryName(componentDir) ?? componentDir;
        string stagingDir = Path.Combine(parentDir, $"{Path.GetFileName(componentDir)}{StagingDirectorySuffix}");

        DeleteDirectoryIfExists(stagingDir);
        Directory.CreateDirectory(stagingDir);

        _logger.LogInformation($"Extracting OpenVINO package to staging directory {stagingDir}...");

        // Extract in a background thread to avoid blocking the async context
        await Task.Run(() =>
        {
            cancellationToken.ThrowIfCancellationRequested();
            ZipFile.ExtractToDirectory(archivePath, stagingDir, overwriteFiles: true);
        }, cancellationToken).ConfigureAwait(false);

        // Swap staging into place only after successful extraction.
        if (Directory.Exists(componentDir))
        {
            DeleteDirectoryIfExists(componentDir);
        }

        Directory.Move(stagingDir, componentDir);

        _logger.LogInformation("OpenVINO package extraction complete.");
        return componentDir;
    }

    private void CleanupPartialFiles(string tempFilePath, string componentDir)
    {
        DeleteFileIfExists(tempFilePath);

        string parentDir = Path.GetDirectoryName(componentDir) ?? componentDir;
        string stagingDir = Path.Combine(parentDir, $"{Path.GetFileName(componentDir)}{StagingDirectorySuffix}");
        DeleteDirectoryIfExists(stagingDir);

        // If the component directory only contains partial extraction artifacts
        // (no marker of a successful install), remove it entirely
        if (Directory.Exists(componentDir) && !_componentStore.IsInstalled(ComponentId))
        {
            try
            {
                Directory.Delete(componentDir, recursive: true);
                _logger.LogInformation("Cleaned up partial OpenVINO component files.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning("Failed to clean up partial OpenVINO component files.", ex);
            }
        }
    }

    private static bool IsComponentDirectoryLikelyInUse(string componentDir)
    {
        try
        {
            if (!Directory.Exists(componentDir))
            {
                return false;
            }

            foreach (string file in Directory.EnumerateFiles(componentDir, "*", SearchOption.AllDirectories))
            {
                using FileStream stream = new(file, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }

            return false;
        }
        catch
        {
            return true;
        }
    }

    private void DeleteDirectoryIfExists(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to clean up directory '{path}'.", ex);
        }
    }

    private static void DeleteFileIfExists(string path)
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
            // Best-effort cleanup only.
        }
    }
}
