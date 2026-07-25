using Trackdub.Contracts.Diagnostics;
using Trackdub.Domain;
using Trackdub.Infrastructure.Diagnostics;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

public sealed class DiagnosticsCollectorTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task CollectAsync_marks_missing_model_cache_roots_as_missing()
    {
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        var store = new LocalModelCacheRecordStore(storagePaths);
        await store.SaveAsync(
            [
                new LocalModelCacheRecord("missing-model", Path.Combine(tempRoot, "missing-model"), "main", "sha", DateTimeOffset.UtcNow)
            ],
            TestContext.Current.CancellationToken);

        DiagnosticsSnapshot snapshot = await new DiagnosticsCollector(storagePaths, store).CollectAsync(TestContext.Current.CancellationToken);

        ModelCacheEntry entry = Assert.Single(snapshot.ModelCacheEntries);
        Assert.Equal("missing-model", entry.ModelId);
        Assert.Equal(Contracts.Diagnostics.ModelCacheState.Missing, entry.State);
    }

    [Fact]
    public async Task CollectAsync_marks_empty_model_cache_directories_as_corrupt()
    {
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        string modelRoot = Path.Combine(tempRoot, "empty-model");
        Directory.CreateDirectory(modelRoot);
        var store = new LocalModelCacheRecordStore(storagePaths);
        await store.SaveAsync(
            [
                new LocalModelCacheRecord("empty-model", modelRoot, "main", "sha", DateTimeOffset.UtcNow)
            ],
            TestContext.Current.CancellationToken);

        DiagnosticsSnapshot snapshot = await new DiagnosticsCollector(storagePaths, store).CollectAsync(TestContext.Current.CancellationToken);

        ModelCacheEntry entry = Assert.Single(snapshot.ModelCacheEntries);
        Assert.Equal("empty-model", entry.ModelId);
        Assert.Equal(Contracts.Diagnostics.ModelCacheState.Corrupt, entry.State);
        Assert.Contains("empty", entry.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CollectAsync_returns_deterministic_deduplicated_numeric_log_paths()
    {
        var storagePaths = new TrackdubStoragePaths(tempRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(storagePaths.LogFilePath)!);
        File.WriteAllText(storagePaths.LogFilePath, "active");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(storagePaths.LogFilePath)!, "trackdub.10.log"), "archive 10");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(storagePaths.LogFilePath)!, "trackdub.2.log"), "archive 2");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(storagePaths.LogFilePath)!, "trackdub.1.log"), "archive 1");
        var store = new LocalModelCacheRecordStore(storagePaths);

        DiagnosticsSnapshot snapshot = await new DiagnosticsCollector(storagePaths, store).CollectAsync(TestContext.Current.CancellationToken);

        string[] fileNames = snapshot.LogFilePaths
            .Select(Path.GetFileName)
            .ToArray()!;
        Assert.Equal(["trackdub.1.log", "trackdub.2.log", "trackdub.10.log", "trackdub.log"], fileNames);
        Assert.Equal(fileNames.Distinct(StringComparer.OrdinalIgnoreCase).Count(), fileNames.Length);
    }

    public void Dispose()
    {
        if (Directory.Exists(tempRoot))
        {
            Directory.Delete(tempRoot, recursive: true);
        }
    }
}
