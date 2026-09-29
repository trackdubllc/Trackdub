using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

// Direct coverage for the index read path: the store buffers the file and deserializes from memory,
// so the multi-record case and the full variant round trip (including provenance) must survive it.
public sealed class LocalModelCacheRecordStoreTests : IDisposable
{
    private readonly string tempRoot = Path.Join(
        Path.GetTempPath(),
        "Trackdub.LocalModelCacheRecordStore.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task LoadAsync_returns_empty_when_index_file_is_absent()
    {
        LocalModelCacheRecordStore store = CreateStore();

        Assert.Empty(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.False(File.Exists(CreateStoragePaths().ModelCacheIndexPath));
    }

    [Fact]
    public async Task LoadAsync_reads_every_record_and_keeps_variant_provenance()
    {
        LocalModelCacheRecordStore store = CreateStore();
        LocalModelCacheRecord[] records = [.. Enumerable.Range(0, 25).Select(BuildRecord)];
        await store.SaveAsync(records, TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(records.Length, loaded.Count);
        Assert.Equal(records.Select(record => record.ModelId), loaded.Select(record => record.ModelId));
        LocalModelVariantRecord variant = Assert.Single(loaded[^1].Variants);
        Assert.Equal("alias-24", variant.Alias);
        Assert.Equal(["encoder.onnx", "decoder.onnx"], variant.ComponentRelativePaths);
        Assert.Equal(ExecutionProviderKind.TensorRTRtx, variant.ExecutionProvider);
        ModelOptimizedVariantProvenance provenance = Assert.IsType<ModelOptimizedVariantProvenance>(variant.Provenance);
        Assert.Equal("olive-0.9.0", provenance.OliveVersion);
        Assert.Equal(
            [ModelOptimizationOperation.Compression, ModelOptimizationOperation.Registration],
            provenance.Operations);
        Assert.Equal(ModelOptimizationExpectedOutput.OnnxComponents, provenance.OutputKind);
        Assert.Equal(["script-a"], provenance.ScriptIdentifiers);
    }

    [Fact]
    public async Task LoadAsync_reports_malformed_index_content()
    {
        LocalModelCacheRecordStore store = CreateStore();
        TrackdubStoragePaths storagePaths = CreateStoragePaths();
        Directory.CreateDirectory(storagePaths.ModelCacheDirectory);
        File.WriteAllText(storagePaths.ModelCacheIndexPath, "{ not json");

        await Assert.ThrowsAsync<JsonException>(() => store.LoadAsync(TestContext.Current.CancellationToken));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
        catch (IOException)
        {
            // best-effort cleanup
        }
    }

    private static LocalModelCacheRecord BuildRecord(int index) => new(
        $"model-{index}",
        Path.Join("D:/models", $"model-{index}"),
        "rev-1",
        new string((char)('a' + (index % 26)), 64),
        new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
        IntegrityFailed: false,
        index == 24 ? [BuildVariant(index)] : []);

    private static LocalModelVariantRecord BuildVariant(int index) => new(
        $"alias-{index}",
        Path.Join("D:/models", $"model-{index}", "optimized"),
        "encoder.onnx",
        ["encoder.onnx", "decoder.onnx"],
        "olive",
        ExecutionProviderKind.TensorRTRtx,
        "fp16",
        new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        "rev-1",
        SourceModelSha256: new string('c', 64),
        IntegrityFailed: false,
        Provenance: new ModelOptimizedVariantProvenance(
            "olive-0.9.0",
            "optimize",
            [ModelOptimizationOperation.Compression, ModelOptimizationOperation.Registration],
            "tensorrt-rtx",
            "gpu",
            RecipeConfigPath: null,
            RecipeConfigSha256: null,
            QuantizationMethod: "fp16",
            Evaluator: null,
            ModelOptimizationExpectedOutput.OnnxComponents,
            ModelOptimizationFallbackPolicy.None,
            ScriptIdentifiers: ["script-a"]));

    private LocalModelCacheRecordStore CreateStore() => new(CreateStoragePaths());

    private TrackdubStoragePaths CreateStoragePaths()
    {
        Directory.CreateDirectory(tempRoot);
        return new TrackdubStoragePaths(tempRoot);
    }
}
