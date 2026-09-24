namespace Trackdub.Contracts;

public sealed record EpContextWarmItem(
    string SourceModelPath,
    string Status,
    string? EpContextPath,
    double CompileMilliseconds,
    double WarmLoadMilliseconds,
    string? Detail);

public sealed record EpContextWarmReport(
    string EngineCacheDirectory,
    string EnvironmentFingerprint,
    int CompiledCount,
    int ReusedCount,
    int SkippedCount,
    int FailedCount,
    IReadOnlyList<EpContextWarmItem> Items);

/// <summary>
/// Ahead-of-time EP-context precompile plus residual runtime-cache warmup.
/// Run after install, app update, GPU swap, driver change, or TRT RTX EP bump.
/// </summary>
public interface IEpContextWarmupService
{
    Task<EpContextWarmReport> WarmAsync(
        IReadOnlyList<string>? sourceModelPaths = null,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default);
}
