using System.Security.Cryptography;
using System.Text.Json;
using Trackdub.Application.Projects;
using Trackdub.Infrastructure.FileSystem;

namespace Trackdub.Infrastructure.Tests;

public sealed class FileSystemArtifactStoreTests
{
    [Fact]
    public async Task CommitAsync_moves_temp_file_atomically_into_place()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "AtomicWrite.trackdub");
        var store = new FileSystemArtifactStore(projectRoot);
        await store.EnsureLayoutAsync(TestContext.Current.CancellationToken);
        var handle = store.CreateWriteHandle(ProjectArtifactPaths.WaveformSummaryRelativePath);

        await File.WriteAllTextAsync(handle.TemporaryPath, "{\"ok\":true}", TestContext.Current.CancellationToken);
        Assert.False(File.Exists(handle.FinalPath));

        await store.CommitAsync(handle, TestContext.Current.CancellationToken);

        Assert.True(File.Exists(handle.FinalPath));
        Assert.False(File.Exists(handle.TemporaryPath));
    }

    [Fact]
    public async Task CommitAsync_overwrites_existing_artifact()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Overwrite.trackdub");
        var store = new FileSystemArtifactStore(projectRoot);
        await store.EnsureLayoutAsync(TestContext.Current.CancellationToken);
        var first = store.CreateWriteHandle(ProjectArtifactPaths.WaveformSummaryRelativePath);
        await File.WriteAllTextAsync(first.TemporaryPath, "old", TestContext.Current.CancellationToken);
        await store.CommitAsync(first, TestContext.Current.CancellationToken);
        var second = store.CreateWriteHandle(ProjectArtifactPaths.WaveformSummaryRelativePath);
        await File.WriteAllTextAsync(second.TemporaryPath, "new", TestContext.Current.CancellationToken);

        await store.CommitAsync(second, TestContext.Current.CancellationToken);

        Assert.Equal("new", await File.ReadAllTextAsync(second.FinalPath));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void IsTransientCommitFailure_identifies_windows_lock_failures(bool unauthorized)
    {
        Exception exception = unauthorized
            ? new UnauthorizedAccessException("locked")
            : new IOException("locked");

        Assert.True(FileSystemArtifactStore.IsTransientCommitFailureForTesting(exception));
    }

    [Fact]
    public async Task WriteJsonAsync_and_fingerprint_service_produce_expected_hash()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "HashCheck.trackdub");
        var store = new FileSystemArtifactStore(projectRoot);
        var fingerprintService = new Sha256FileFingerprintService();
        await store.EnsureLayoutAsync(TestContext.Current.CancellationToken);

        await store.WriteJsonAsync(
            ProjectArtifactPaths.WaveformSummaryRelativePath,
            new { ok = true, buckets = 4 },
            TestContext.Current.CancellationToken);

        string finalPath = store.GetPath(ProjectArtifactPaths.WaveformSummaryRelativePath);
        string expectedHash = Convert.ToHexString(
                SHA256.HashData(await File.ReadAllBytesAsync(finalPath)))
            .ToLowerInvariant();

        var fingerprint = await fingerprintService.ComputeAsync(finalPath, TestContext.Current.CancellationToken);

        Assert.Equal(expectedHash, fingerprint.Sha256);

        using JsonDocument document = JsonDocument.Parse(await File.ReadAllTextAsync(finalPath));
        Assert.True(document.RootElement.GetProperty("ok").GetBoolean());
    }

    [Fact]
    public async Task FingerprintService_bounds_cache_entries()
    {
        string root = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "FingerprintCache");
        try
        {
            Directory.CreateDirectory(root);
            var fingerprintService = new Sha256FileFingerprintService();

            for (int i = 0; i < Sha256FileFingerprintService.MaxCachedFingerprints + 5; i++)
            {
                string path = Path.Combine(root, $"{i}.txt");
                await File.WriteAllTextAsync(path, i.ToString());
                await fingerprintService.ComputeAsync(path, TestContext.Current.CancellationToken);
            }

            Assert.True(fingerprintService.CachedFingerprintCountForTesting <= Sha256FileFingerprintService.MaxCachedFingerprints);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Fact]
    public async Task FingerprintService_keeps_recently_used_entries_when_cache_is_full()
    {
        string root = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "FingerprintCacheLru");
        try
        {
            Directory.CreateDirectory(root);
            var fingerprintService = new Sha256FileFingerprintService();
            string sentinelPath = Path.Combine(root, "sentinel.txt");
            string firstFillerPath = Path.Combine(root, "filler-0.txt");

            await File.WriteAllTextAsync(sentinelPath, "sentinel", TestContext.Current.CancellationToken);
            await fingerprintService.ComputeAsync(sentinelPath, TestContext.Current.CancellationToken);

            for (int i = 0; i < Sha256FileFingerprintService.MaxCachedFingerprints - 1; i++)
            {
                string path = Path.Combine(root, $"filler-{i}.txt");
                await File.WriteAllTextAsync(path, i.ToString());
                await fingerprintService.ComputeAsync(path, TestContext.Current.CancellationToken);
            }

            await fingerprintService.ComputeAsync(sentinelPath, TestContext.Current.CancellationToken);

            string overflowPath = Path.Combine(root, "overflow.txt");
            await File.WriteAllTextAsync(overflowPath, "overflow", TestContext.Current.CancellationToken);
            await fingerprintService.ComputeAsync(overflowPath, TestContext.Current.CancellationToken);

            Assert.True(fingerprintService.ContainsCachedFingerprintForTesting(sentinelPath));
            Assert.False(fingerprintService.ContainsCachedFingerprintForTesting(firstFillerPath));
            Assert.True(fingerprintService.CachedFingerprintCountForTesting <= Sha256FileFingerprintService.MaxCachedFingerprints);
        }
        finally
        {
            DeleteDirectory(root);
        }
    }

    [Theory]
    [InlineData(@"D:\absolute\artifact.json")]
    [InlineData(@"\\server\share\artifact.json")]
    public void CreateWriteHandle_RejectsAbsolutePath(string path)
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "AbsolutePath.trackdub");
        var store = new FileSystemArtifactStore(projectRoot);

        Assert.Throws<InvalidOperationException>(() => store.CreateWriteHandle(path));
    }

    [Theory]
    [InlineData("artifacts/CON.txt")]
    [InlineData("artifacts/NUL.json")]
    [InlineData("artifacts/folder/COM1.log")]
    [InlineData("artifacts/folder/LPT9. ")]
    public void CreateWriteHandle_RejectsWindowsReservedDeviceNamesWithSuffixes(string path)
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "ReservedPath.trackdub");
        var store = new FileSystemArtifactStore(projectRoot);

        Assert.Throws<InvalidOperationException>(() => store.CreateWriteHandle(path));
    }

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path))
        {
            Directory.Delete(path, recursive: true);
        }
    }
}
