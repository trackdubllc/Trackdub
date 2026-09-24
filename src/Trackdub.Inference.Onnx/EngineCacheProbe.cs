using Trackdub.Domain;

namespace Trackdub.Inference.Onnx;

/// <summary>
/// Captures engine-cache size before/after a session create so cold load can be attributed
/// honestly: a cache write means TensorRT compiled engines during construct; a non-empty
/// cache with no growth means construct was deserialize-dominated. Does not invent a
/// millisecond split ORT will not expose.
/// </summary>
public static class EngineCacheProbe
{
    public readonly record struct Snapshot(
        string? DirectoryPath,
        bool DirectoryExists,
        int FileCount,
        long TotalBytes);

    public static Snapshot Capture()
    {
        string? directory = ResolveCacheDirectory();
        if (directory is null || !Directory.Exists(directory))
        {
            return new Snapshot(directory, DirectoryExists: false, FileCount: 0, TotalBytes: 0);
        }

        int fileCount = 0;
        long totalBytes = 0;
        try
        {
            foreach (string file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories))
            {
                fileCount++;
                totalBytes += new FileInfo(file).Length;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort: attribution must never fail a benchmark.
        }

        return new Snapshot(directory, DirectoryExists: true, fileCount, totalBytes);
    }

    public static string ClassifyOutcome(Snapshot before, Snapshot after, BenchmarkProviderPreference provider)
    {
        if (provider is not (BenchmarkProviderPreference.TensorRtRtx or BenchmarkProviderPreference.TensorRt or BenchmarkProviderPreference.Migraphx))
        {
            return "not-applicable";
        }

        long bytesAdded = Math.Max(0, after.TotalBytes - before.TotalBytes);
        int filesAdded = Math.Max(0, after.FileCount - before.FileCount);

        if (bytesAdded > 0 || filesAdded > 0)
        {
            return "wrote";
        }

        if (before.FileCount > 0)
        {
            return "hit";
        }

        return "empty";
    }

    /// <summary>
    /// Evidence-based dominant phase of session construct. We only claim compile when the
    /// engine cache actually grew; otherwise construct is weights/deserialize work.
    /// </summary>
    public static string ClassifyDominantPhase(string? engineCacheOutcome, BenchmarkProviderPreference provider)
    {
        if (engineCacheOutcome is "wrote")
        {
            return "engine-compile";
        }

        if (engineCacheOutcome is "hit")
        {
            return "model-deserialize";
        }

        if (engineCacheOutcome is "not-applicable")
        {
            return "model-deserialize";
        }

        return "unknown";
    }

    public static string FormatNote(
        double coldLoadMilliseconds,
        string engineCacheOutcome,
        Snapshot before,
        Snapshot after,
        string dominantPhase)
    {
        long bytesAdded = Math.Max(0, after.TotalBytes - before.TotalBytes);
        int filesAdded = Math.Max(0, after.FileCount - before.FileCount);
        return
            $"Cold load breakdown: total={coldLoadMilliseconds:0.#} ms; "
            + $"engine cache={engineCacheOutcome} (+{filesAdded} file(s), +{bytesAdded} byte(s)); "
            + $"dominant={dominantPhase}.";
    }

    private static string? ResolveCacheDirectory()
    {
        string? engineCacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_ENGINE_CACHE_ROOT");
        if (!string.IsNullOrWhiteSpace(engineCacheRoot))
        {
            return Path.GetFullPath(Environment.ExpandEnvironmentVariables(engineCacheRoot));
        }

        string? cacheRoot = Environment.GetEnvironmentVariable("TRACKDUB_CACHE_ROOT");
        if (!string.IsNullOrWhiteSpace(cacheRoot))
        {
            return Path.Combine(
                Path.GetFullPath(Environment.ExpandEnvironmentVariables(cacheRoot)),
                "EngineCache");
        }

        string localAppDataRoot = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppDataRoot))
        {
            localAppDataRoot = AppContext.BaseDirectory;
        }

        return Path.Combine(localAppDataRoot, "Trackdub", "EngineCache");
    }
}
