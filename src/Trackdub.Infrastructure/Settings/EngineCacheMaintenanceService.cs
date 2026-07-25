using System.Security;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Settings;

public sealed class EngineCacheMaintenanceService(IAppStoragePaths storagePaths) : IEngineCacheMaintenanceService
{
    public EngineCacheDescription Describe()
    {
        string directory = storagePaths.EngineCacheDirectory;
        if (!Directory.Exists(directory))
        {
            return new EngineCacheDescription(directory, ApproximateSizeBytes: 0, FileCount: 0, DirectoryExists: false);
        }

        long totalBytes = 0;
        int fileCount = 0;
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                try
                {
                    totalBytes += new FileInfo(filePath).Length;
                }
                catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
                {
                    // Best-effort size for doctor output; skip unreadable entries.
                }
            }
        }
        catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
        {
            // Best-effort enumeration for doctor output; return partial counts.
        }

        return new EngineCacheDescription(directory, totalBytes, fileCount, DirectoryExists: true);
    }

    public EngineCacheClearResult Clear()
    {
        string directory = storagePaths.EngineCacheDirectory;
        if (!Directory.Exists(directory))
        {
            return new EngineCacheClearResult(
                directory,
                FilesRemoved: 0,
                FilesSkipped: 0,
                BytesFreed: 0,
                DirectoryExisted: false);
        }

        int filesRemoved = 0;
        int filesSkipped = 0;
        long bytesFreed = 0;
        try
        {
            foreach (string filePath in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                try
                {
                    var info = new FileInfo(filePath);
                    if ((info.Attributes & FileAttributes.ReadOnly) != 0)
                    {
                        filesSkipped++;
                        continue;
                    }
                    long length = info.Length;
                    File.Delete(filePath);
                    filesRemoved++;
                    bytesFreed += length;
                }
                catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
                {
                    filesSkipped++;
                }
            }
        }
        catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
        {
            // Continue with directory cleanup; caller sees partial counts.
        }

        RemoveEmptySubdirectories(directory);

        return new EngineCacheClearResult(directory, filesRemoved, filesSkipped, bytesFreed, DirectoryExisted: true);
    }

    private static void RemoveEmptySubdirectories(string root)
    {
        foreach (string subdirectory in Directory.EnumerateDirectories(root, "*", SearchOption.AllDirectories).OrderByDescending(Path.GetFullPath))
        {
            try
            {
                if (!Directory.EnumerateFileSystemEntries(subdirectory).Any())
                {
                    Directory.Delete(subdirectory);
                }
            }
            catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
            {
            }
        }
    }

    private static bool IsBestEffortFileAccessFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;
}
