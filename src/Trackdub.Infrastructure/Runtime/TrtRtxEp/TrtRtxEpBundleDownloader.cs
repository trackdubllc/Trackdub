using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Runtime.TrtRtxEp;

public sealed class TrtRtxEpBundleDownloader(
    HttpClient httpClient,
    IApplicationLogger logger)
{
    private const int BufferSize = 65536;

    public async Task<string> DownloadAndInstallAsync(
        string userDataRoot,
        TrtRtxEpBundleManifest manifest,
        IReadOnlyList<string> requiredFileNames,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(requiredFileNames);

        string runtimeIdentifier = TrtRtxEpBundlePathResolver.ResolveRuntimeIdentifier();
        if (!manifest.Packages.TryGetValue(runtimeIdentifier, out TrtRtxEpBundlePackage? package))
        {
            throw new InvalidOperationException(
                $"TensorRT RTX EP manifest does not contain a package entry for '{runtimeIdentifier}'.");
        }

        string version = manifest.ResolveVersion(runtimeIdentifier);
        string installDirectory = TrtRtxEpBundlePathResolver.GetInstallDirectory(
            userDataRoot,
            version,
            manifest.CudaVariant,
            runtimeIdentifier);

        if (IsBundleReady(installDirectory, requiredFileNames))
        {
            progress?.Report($"TensorRT RTX EP bundle already installed at '{installDirectory}'.");
            return installDirectory;
        }

        progress?.Report(
            $"Downloading TensorRT RTX EP ABI v{version} {manifest.CudaVariant} ({runtimeIdentifier})...");
        logger.LogInformation(
            $"Downloading TensorRT RTX EP ABI bundle v{version} {manifest.CudaVariant} for {runtimeIdentifier}.");

        string parentDirectory = Path.GetDirectoryName(installDirectory)
            ?? throw new InvalidOperationException("Install directory path has no parent.");
        Directory.CreateDirectory(parentDirectory);

        string tempArchivePath = Path.Combine(
            parentDirectory,
            $"trt-rtx-ep-{Guid.NewGuid():N}{GetArchiveExtension(package.ArchiveKind)}");
        string tempExtractDirectory = Path.Combine(parentDirectory, $"trt-rtx-ep-extract-{Guid.NewGuid():N}");
        string tempInstallDirectory = Path.Combine(parentDirectory, $"trt-rtx-ep-staging-{Guid.NewGuid():N}");

        try
        {
            await DownloadArchiveAsync(package, tempArchivePath, cancellationToken).ConfigureAwait(false);
            await VerifyArchiveAsync(tempArchivePath, package, cancellationToken).ConfigureAwait(false);

            Directory.CreateDirectory(tempExtractDirectory);
            await ExtractArchiveAsync(package.ArchiveKind, tempArchivePath, tempExtractDirectory, cancellationToken)
                .ConfigureAwait(false);

            Directory.CreateDirectory(tempInstallDirectory);
            CopyNativeLibrariesFlat(tempExtractDirectory, tempInstallDirectory);
            ValidateRequiredFiles(tempInstallDirectory, requiredFileNames);

            if (Directory.Exists(installDirectory))
            {
                Directory.Delete(installDirectory, recursive: true);
            }

            Directory.Move(tempInstallDirectory, installDirectory);
            progress?.Report($"TensorRT RTX EP bundle installed to '{installDirectory}'.");
            logger.LogInformation($"TensorRT RTX EP ABI bundle installed at '{installDirectory}'.");
            return installDirectory;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning($"TensorRT RTX EP bundle install failed: {ex.Message}");
            throw;
        }
        finally
        {
            DeleteDirectoryIfExists(tempExtractDirectory);
            DeleteDirectoryIfExists(tempInstallDirectory);
            DeleteFileIfExists(tempArchivePath);
        }
    }

    public static bool IsBundleReady(string installDirectory, IReadOnlyList<string> requiredFileNames)
    {
        if (string.IsNullOrWhiteSpace(installDirectory) || !Directory.Exists(installDirectory))
        {
            return false;
        }

        return requiredFileNames.All(fileName => File.Exists(Path.Combine(installDirectory, fileName)));
    }

    private async Task DownloadArchiveAsync(
        TrtRtxEpBundlePackage package,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient
            .GetAsync(package.ArchiveUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = new(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None, BufferSize);
        await contentStream.CopyToAsync(output, cancellationToken).ConfigureAwait(false);
        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        TrtRtxEpBundlePackage package,
        CancellationToken cancellationToken)
    {
        FileInfo fileInfo = new(archivePath);
        if (package.SizeBytes > 0 && fileInfo.Length != package.SizeBytes)
        {
            throw new InvalidOperationException(
                $"TensorRT RTX EP archive size mismatch. Expected {package.SizeBytes}, got {fileInfo.Length}.");
        }

        if (string.IsNullOrWhiteSpace(package.Sha256))
        {
            return;
        }

        await using FileStream stream = File.OpenRead(archivePath);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false))
            .ToLowerInvariant();
        if (!string.Equals(actualHash, package.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("TensorRT RTX EP archive checksum verification failed.");
        }
    }

    private static async Task ExtractArchiveAsync(
        string archiveKind,
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        switch (archiveKind.ToLowerInvariant())
        {
            case "zip":
                await Task.Run(() => ExtractZipPreservingSymlinks(archivePath, destinationDirectory), cancellationToken)
                    .ConfigureAwait(false);
                return;
            case "tar.gz":
                await ExtractTarGzAsync(archivePath, destinationDirectory, cancellationToken).ConfigureAwait(false);
                return;
            default:
                throw new InvalidOperationException($"Unsupported TensorRT RTX EP archive kind '{archiveKind}'.");
        }
    }

    private static async Task ExtractTarGzAsync(
        string archivePath,
        string destinationDirectory,
        CancellationToken cancellationToken)
    {
        await using FileStream fileStream = File.OpenRead(archivePath);
        await using GZipStream gzipStream = new(fileStream, CompressionMode.Decompress);
        await TarFile.ExtractToDirectoryAsync(gzipStream, destinationDirectory, overwriteFiles: true, cancellationToken)
            .ConfigureAwait(false);
    }

    private static void CopyNativeLibrariesFlat(string sourceRoot, string destinationRoot)
    {
        IEnumerable<string> patterns = OperatingSystem.IsWindows()
            ? ["*.dll"]
            : ["*.so", "*.so.*"];

        foreach (string pattern in patterns)
        {
            foreach (string sourcePath in Directory.EnumerateFiles(sourceRoot, pattern, SearchOption.AllDirectories))
            {
                if (sourcePath.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                string destinationPath = Path.Combine(destinationRoot, Path.GetFileName(sourcePath));
                string? linkTarget = OperatingSystem.IsWindows() ? null : new FileInfo(sourcePath).LinkTarget;
                if (linkTarget is not null && IsFlatFileName(linkTarget))
                {
                    // Keep SONAME chains as links; copying would duplicate the 200+ MB runtime per alias.
                    DeleteFileIfExists(destinationPath);
                    File.CreateSymbolicLink(destinationPath, linkTarget);
                    continue;
                }

                File.Copy(sourcePath, destinationPath, overwrite: true);
            }
        }
    }

    // Unix-built zips (the linux cu13 EP ABI asset) store SONAME symlinks as tiny entries whose
    // content is the link target. ZipFile.ExtractToDirectory writes those as text files, which
    // dlopen rejects, so symlink entries are recreated as links (Unix) or resolved copies (Windows).
    private const int UnixFileTypeMask = 0xF000;
    private const int UnixSymlinkFileType = 0xA000;
    private const int MaxSymlinkDepth = 8;

    internal static void ExtractZipPreservingSymlinks(string archivePath, string destinationDirectory)
    {
        StringComparison pathComparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        string root = Path.GetFullPath(destinationDirectory);
        string rootPrefix = Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar;
        var links = new Dictionary<string, string>(
            OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

        using (ZipArchive archive = ZipFile.OpenRead(archivePath))
        {
            foreach (ZipArchiveEntry entry in archive.Entries)
            {
                string destinationPath = Path.GetFullPath(Path.Combine(root, entry.FullName));
                if (!destinationPath.StartsWith(rootPrefix, pathComparison))
                {
                    throw new InvalidOperationException(
                        $"TensorRT RTX EP archive entry '{entry.FullName}' resolves outside the extract directory.");
                }

                if (string.IsNullOrEmpty(entry.Name))
                {
                    Directory.CreateDirectory(destinationPath);
                    continue;
                }

                Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
                if (((entry.ExternalAttributes >> 16) & UnixFileTypeMask) == UnixSymlinkFileType)
                {
                    using StreamReader reader = new(entry.Open());
                    string target = reader.ReadToEnd().Trim();
                    if (!IsFlatFileName(target))
                    {
                        throw new InvalidOperationException(
                            $"TensorRT RTX EP archive symlink '{entry.FullName}' has unsupported target '{target}'.");
                    }

                    links[destinationPath] = target;
                    continue;
                }

                entry.ExtractToFile(destinationPath, overwrite: true);
            }
        }

        foreach ((string linkPath, string target) in links)
        {
            if (!OperatingSystem.IsWindows())
            {
                File.CreateSymbolicLink(linkPath, target);
                continue;
            }

            string resolved = Path.Combine(Path.GetDirectoryName(linkPath)!, target);
            for (int depth = 0; links.TryGetValue(resolved, out string? next); depth++)
            {
                if (depth >= MaxSymlinkDepth)
                {
                    throw new InvalidOperationException(
                        $"TensorRT RTX EP archive symlink chain at '{linkPath}' is too deep or cyclic.");
                }

                resolved = Path.Combine(Path.GetDirectoryName(resolved)!, next);
            }

            if (!File.Exists(resolved))
            {
                throw new InvalidOperationException(
                    $"TensorRT RTX EP archive symlink '{linkPath}' points at missing file '{target}'.");
            }

            File.Copy(resolved, linkPath, overwrite: true);
        }
    }

    private static bool IsFlatFileName(string value) =>
        !string.IsNullOrWhiteSpace(value) &&
        value is not "." and not ".." &&
        value.IndexOfAny(['/', '\\']) < 0;

    private static void ValidateRequiredFiles(string installDirectory, IReadOnlyList<string> requiredFileNames)
    {
        List<string> missing = requiredFileNames
            .Where(fileName => !File.Exists(Path.Combine(installDirectory, fileName)))
            .ToList();

        if (missing.Count > 0)
        {
            throw new InvalidOperationException(
                $"TensorRT RTX EP bundle install is missing required files: {string.Join(", ", missing)}.");
        }
    }

    private static string GetArchiveExtension(string archiveKind) =>
        archiveKind.ToLowerInvariant() switch
        {
            "zip" => ".zip",
            "tar.gz" => ".tar.gz",
            _ => throw new InvalidOperationException($"Unsupported TensorRT RTX EP archive kind '{archiveKind}'.")
        };

    private static void DeleteDirectoryIfExists(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }

    private static void DeleteFileIfExists(string path)
    {
        if (File.Exists(path))
        {
            File.Delete(path);
        }
    }
}
