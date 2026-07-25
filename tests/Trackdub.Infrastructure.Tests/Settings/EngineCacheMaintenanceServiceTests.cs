using Trackdub.Contracts;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests.Settings;

public sealed class EngineCacheMaintenanceServiceTests
{
    [Fact]
    public void Clear_removes_files_preserves_directory_and_reports_bytes()
    {
        string root = Path.Combine(Path.GetTempPath(), $"trackdub-engine-cache-{Guid.NewGuid():N}");
        var paths = new TrackdubStoragePaths(root);
        Directory.CreateDirectory(paths.EngineCacheDirectory);

        string nested = Path.Combine(paths.EngineCacheDirectory, "trt", "session");
        Directory.CreateDirectory(nested);
        string fileOne = Path.Combine(nested, "engine.cache");
        string fileTwo = Path.Combine(paths.EngineCacheDirectory, "root.cache");
        File.WriteAllText(fileOne, new string('a', 128));
        File.WriteAllText(fileTwo, new string('b', 64));

        var service = new EngineCacheMaintenanceService(paths);
        EngineCacheClearResult result = service.Clear();

        Assert.True(result.DirectoryExisted);
        Assert.Equal(paths.EngineCacheDirectory, result.CacheDirectory);
        Assert.Equal(2, result.FilesRemoved);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(192, result.BytesFreed);
        Assert.True(Directory.Exists(paths.EngineCacheDirectory));
        Assert.False(Directory.EnumerateFileSystemEntries(paths.EngineCacheDirectory, "*", SearchOption.AllDirectories).Any());

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }

    [Fact]
    public void Clear_missing_directory_is_idempotent()
    {
        string root = Path.Combine(Path.GetTempPath(), $"trackdub-engine-cache-missing-{Guid.NewGuid():N}");
        var paths = new TrackdubStoragePaths(root);
        var service = new EngineCacheMaintenanceService(paths);

        EngineCacheClearResult result = service.Clear();

        Assert.False(result.DirectoryExisted);
        Assert.Equal(0, result.FilesRemoved);
        Assert.Equal(0, result.FilesSkipped);
        Assert.Equal(0, result.BytesFreed);
    }

    [Fact]
    public void Clear_skips_read_only_files_and_reports_partial_counts()
    {
        string root = Path.Combine(Path.GetTempPath(), $"trackdub-engine-cache-readonly-{Guid.NewGuid():N}");
        var paths = new TrackdubStoragePaths(root);
        Directory.CreateDirectory(paths.EngineCacheDirectory);

        string removable = Path.Combine(paths.EngineCacheDirectory, "removable.cache");
        string readOnly = Path.Combine(paths.EngineCacheDirectory, "readonly.cache");
        File.WriteAllText(removable, new string('a', 32));
        File.WriteAllText(readOnly, new string('b', 16));
        File.SetAttributes(readOnly, FileAttributes.ReadOnly);

        try
        {
            var service = new EngineCacheMaintenanceService(paths);
            EngineCacheClearResult result = service.Clear();

            Assert.True(result.DirectoryExisted);
            Assert.Equal(1, result.FilesRemoved);
            Assert.Equal(1, result.FilesSkipped);
            Assert.Equal(32, result.BytesFreed);
            Assert.True(File.Exists(readOnly));
            Assert.False(File.Exists(removable));
        }
        finally
        {
            if (File.Exists(readOnly))
            {
                File.SetAttributes(readOnly, FileAttributes.Normal);
            }

            try
            {
                if (File.Exists(removable))
                {
                    File.Delete(removable);
                }

                if (File.Exists(readOnly))
                {
                    File.Delete(readOnly);
                }

                Directory.Delete(root, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    [Fact]
    public void Describe_reports_size_and_count()
    {
        string root = Path.Combine(Path.GetTempPath(), $"trackdub-engine-cache-describe-{Guid.NewGuid():N}");
        var paths = new TrackdubStoragePaths(root);
        Directory.CreateDirectory(paths.EngineCacheDirectory);
        File.WriteAllText(Path.Combine(paths.EngineCacheDirectory, "a.cache"), "12345");

        var service = new EngineCacheMaintenanceService(paths);
        EngineCacheDescription description = service.Describe();

        Assert.True(description.DirectoryExists);
        Assert.Equal(paths.EngineCacheDirectory, description.CacheDirectory);
        Assert.Equal(1, description.FileCount);
        Assert.Equal(5, description.ApproximateSizeBytes);

        try
        {
            Directory.Delete(root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
