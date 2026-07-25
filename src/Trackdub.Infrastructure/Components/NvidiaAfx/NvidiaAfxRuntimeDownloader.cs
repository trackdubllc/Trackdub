using System.IO.Compression;
using System.Security.Cryptography;
using Trackdub.Contracts;
using Trackdub.Infrastructure.Components;

namespace Trackdub.Infrastructure.Components.NvidiaAfx;

public sealed class NvidiaAfxRuntimeDownloader(
    ComponentStore componentStore,
    HttpClient httpClient,
    IApplicationLogger logger)
{
    public const string ComponentId = "nvidia-afx-runtime";
    private const string TempSuffix = ".downloading";
    private const string StagingSuffix = ".staging";

    public async Task<string> DownloadAndInstallAsync(
        NvidiaAfxRuntimePackage package,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(package);

        string componentDirectory = componentStore.GetComponentDirectory(ComponentId);
        string archivePath = Path.Combine(componentDirectory, $"{package.Architecture}{TempSuffix}");
        Directory.CreateDirectory(componentDirectory);

        await DownloadArchiveAsync(package.DownloadUrl, archivePath, package.SizeBytes, progress, cancellationToken).ConfigureAwait(false);
        await VerifyArchiveAsync(archivePath, package.Sha256, package.SizeBytes, cancellationToken).ConfigureAwait(false);

        string installPath = await ExtractAtomicallyAsync(componentDirectory, archivePath, cancellationToken).ConfigureAwait(false);
        componentStore.MarkInstalled(ComponentId);
        progress?.Report(1.0);
        logger.LogInformation($"NVIDIA AFX runtime package installed at '{installPath}' ({package.Architecture}).");
        return installPath;
    }

    private async Task DownloadArchiveAsync(
        string url,
        string destinationPath,
        long expectedBytes,
        IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        using HttpResponseMessage response = await httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using FileStream output = File.Create(destinationPath);
        byte[] buffer = new byte[65536];
        long written = 0;
        int read;
        while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
        {
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            written += read;
            if (expectedBytes > 0)
            {
                progress?.Report((double)written / expectedBytes * 0.9d);
            }
        }
    }

    private static async Task VerifyArchiveAsync(
        string archivePath,
        string expectedSha256,
        long expectedBytes,
        CancellationToken cancellationToken)
    {
        var fileInfo = new FileInfo(archivePath);
        if (expectedBytes > 0 && fileInfo.Length != expectedBytes)
        {
            throw new InvalidOperationException($"NVIDIA AFX runtime archive size mismatch. Expected {expectedBytes}, got {fileInfo.Length}.");
        }

        await using FileStream stream = File.OpenRead(archivePath);
        string actualHash = Convert.ToHexString(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false)).ToLowerInvariant();
        if (!string.Equals(actualHash, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("NVIDIA AFX runtime archive checksum verification failed.");
        }
    }

    private static async Task<string> ExtractAtomicallyAsync(
        string componentDirectory,
        string archivePath,
        CancellationToken cancellationToken)
    {
        string parent = Path.GetDirectoryName(componentDirectory) ?? componentDirectory;
        string staging = Path.Combine(parent, $"{Path.GetFileName(componentDirectory)}{StagingSuffix}");
        if (Directory.Exists(staging))
        {
            Directory.Delete(staging, recursive: true);
        }

        Directory.CreateDirectory(staging);
        await Task.Run(() => ZipFile.ExtractToDirectory(archivePath, staging, overwriteFiles: true), cancellationToken).ConfigureAwait(false);

        if (Directory.Exists(componentDirectory))
        {
            Directory.Delete(componentDirectory, recursive: true);
        }

        Directory.Move(staging, componentDirectory);
        return componentDirectory;
    }
}
