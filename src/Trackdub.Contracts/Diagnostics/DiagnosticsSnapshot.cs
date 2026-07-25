namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// State of a model in the local model cache.
/// </summary>
public enum ModelCacheState
{
    Installed = 1,
    Missing = 2,
    Corrupt = 3
}

/// <summary>
/// Summary of a single model's presence in the local cache.
/// </summary>
public sealed record ModelCacheEntry(
    string ModelId,
    ModelCacheState State,
    string? Detail = null);

/// <summary>
/// A point-in-time snapshot of all diagnostics information for the current application session.
/// </summary>
public sealed record DiagnosticsSnapshot(
    string OsVersion,
    string Architecture,
    string? GpuDescription,
    bool DirectMlAvailable,
    string? OnnxRuntimeVersion,
    string? WindowsAppSdkVersion,
    int DbSchemaVersion,
    IReadOnlyList<ModelCacheEntry> ModelCacheEntries,
    IReadOnlyList<string> LogFilePaths,
    DateTimeOffset CollectedAtUtc);
