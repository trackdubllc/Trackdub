namespace Trackdub.Contracts;

public sealed record EngineCacheClearResult(
    string CacheDirectory,
    int FilesRemoved,
    int FilesSkipped,
    long BytesFreed,
    bool DirectoryExisted);

public sealed record EngineCacheDescription(
    string CacheDirectory,
    long ApproximateSizeBytes,
    int FileCount,
    bool DirectoryExists);

/// <summary>
/// Maintains ONNX/TRT engine runtime cache files under the user cache root.
/// </summary>
public interface IEngineCacheMaintenanceService
{
    EngineCacheDescription Describe();

    EngineCacheClearResult Clear();
}
