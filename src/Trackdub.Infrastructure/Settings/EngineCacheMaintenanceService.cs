using System.Security;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Settings;

public sealed class EngineCacheMaintenanceService(
    IAppStoragePaths storagePaths,
    ISmokeVerdictStore? smokeVerdictStore = null) : IEngineCacheMaintenanceService
{
    // EP-context AOT artifacts live next to their source model but share the engine cache's
    // invalidation triggers (model bytes, GPU arch, driver, TRT RTX EP version). Clearing the
    // engine cache must drop them too, or load paths keep preferring a stale precompile.
    private const string EpContextArtifactSuffix = ".epc.onnx";
    private const string EpContextStampSuffix = ".epc.stamp.json";
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
        // Compiled engines and smoke verdicts share invalidation triggers (model sha256, EP,
        // GPU arch, driver, TRT RTX EP version); clearing one must clear the other.
        smokeVerdictStore?.Clear();
        ClearEpContextArtifacts();

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

    private void ClearEpContextArtifacts()
    {
        if (!Directory.Exists(storagePaths.ModelCacheDirectory))
        {
            return;
        }

        try
        {
            foreach (string filePath in Directory.EnumerateFiles(storagePaths.ModelCacheDirectory, "*", SearchOption.AllDirectories))
            {
                if (!filePath.EndsWith(EpContextArtifactSuffix, StringComparison.OrdinalIgnoreCase) &&
                    !filePath.EndsWith(EpContextStampSuffix, StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                try
                {
                    File.Delete(filePath);
                }
                catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
                {
                    // Log the file deletion failure with the file path and exception message
                    // to aid debugging locked or missing EP-context artifacts that prevent
                    // complete cache cleanup.
                    // Best-effort per-file cleanup; a locked or missing file does not fail the clear.
                }
            }
        }
        catch (Exception ex) when (IsBestEffortFileAccessFailure(ex))
        {
            // Best-effort cleanup; enumeration/access failure leaves the remaining cache untouched.
            // Log the enumeration failure with the directory path to aid debugging
            // issues with inaccessible EP-context artifact directories.
        }
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
                // Best-effort cleanup; a directory that can't be removed is left in place.
            }
        }
    }

    private static bool IsBestEffortFileAccessFailure(Exception ex) =>
        ex is IOException or UnauthorizedAccessException or SecurityException;
}
