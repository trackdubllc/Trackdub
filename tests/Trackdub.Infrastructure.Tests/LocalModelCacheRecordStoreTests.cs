using System.Text.Json;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Tests;

// Direct coverage for the index read and persist path: the store buffers the file and deserializes
// from memory, and the on-disk format is pinned so no read-path change can alter it silently.
public sealed class LocalModelCacheRecordStoreTests : IDisposable
{
    // Pinned output for BuildGoldenRecords(), with newlines normalised so the guard is cross-platform.
    private const string ExpectedIndexJson = """
        [
          {
            "ModelId": "example/model",
            "RootPath": "D:/models/example",
            "Revision": "main",
            "Sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
            "CachedAtUtc": "2026-01-01T00:00:00+00:00",
            "IntegrityFailed": false,
            "Variants": [
              {
                "Alias": "olive-cuda-fp16",
                "RootPath": "D:/models/example/optimized/olive-cuda-fp16",
                "EntryRelativePath": "encoder.onnx",
                "ComponentRelativePaths": [
                  "encoder.onnx",
                  "decoder.onnx"
                ],
                "OptimizerId": "olive",
                "ExecutionProvider": 6,
                "Precision": "fp16",
                "CreatedAtUtc": "2026-01-02T00:00:00+00:00",
                "SourceModelRevision": "main",
                "SourceModelSha256": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc",
                "IntegrityFailed": false,
                "Provenance": {
                  "OliveVersion": "olive-0.9.0",
                  "CommandKind": "optimize",
                  "Operations": [
                    3,
                    9
                  ],
                  "OliveProvider": "tensorrt-rtx",
                  "Device": "gpu",
                  "RecipeConfigPath": null,
                  "RecipeConfigSha256": null,
                  "QuantizationMethod": "fp16",
                  "Evaluator": null,
                  "OutputKind": 0,
                  "FallbackPolicy": 0,
                  "ScriptIdentifiers": [
                    "script-a"
                  ]
                }
              }
            ]
          },
          {
            "ModelId": "example/model-b",
            "RootPath": "D:/models/example-b",
            "Revision": "rev-2",
            "Sha256": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
            "CachedAtUtc": "2026-01-02T00:00:00+00:00",
            "IntegrityFailed": false,
            "Variants": []
          }
        ]
        """;

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

    // Byte-level guard on the persisted contract: naming policy, indentation, enum representation,
    // property order and field loss all have to fail here, not silently ship.
    [Fact]
    public async Task Index_bytes_are_pinned_to_the_current_wire_format()
    {
        LocalModelCacheRecordStore store = CreateStore();
        await store.SaveAsync(BuildGoldenRecords(), TestContext.Current.CancellationToken);

        string written = NormalizeNewLines(await File.ReadAllTextAsync(
            CreateStoragePaths().ModelCacheIndexPath,
            TestContext.Current.CancellationToken));

        Assert.Equal(ExpectedIndexJson, written);
    }

    [Fact]
    public async Task Index_bytes_match_the_pre_source_generation_writer()
    {
        LocalModelCacheRecordStore store = CreateStore();
        LocalModelCacheRecord[] records = BuildGoldenRecords();
        await store.SaveAsync(records, TestContext.Current.CancellationToken);
        byte[] written = await File.ReadAllBytesAsync(
            CreateStoragePaths().ModelCacheIndexPath,
            TestContext.Current.CancellationToken);

        // The options LocalModelCacheRecordStore declared before source generation.
        byte[] beforeChange = JsonSerializer.SerializeToUtf8Bytes(records, new JsonSerializerOptions { WriteIndented = true });

        Assert.Equal(beforeChange, written);
    }

    [Fact]
    public async Task Index_bytes_survive_a_load_and_save_round_trip()
    {
        LocalModelCacheRecordStore store = CreateStore();
        await store.SaveAsync(BuildGoldenRecords(), TestContext.Current.CancellationToken);
        byte[] first = await File.ReadAllBytesAsync(CreateStoragePaths().ModelCacheIndexPath, TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> reloaded = await store.LoadAsync(TestContext.Current.CancellationToken);
        await store.SaveAsync(reloaded, TestContext.Current.CancellationToken);
        byte[] second = await File.ReadAllBytesAsync(CreateStoragePaths().ModelCacheIndexPath, TestContext.Current.CancellationToken);

        Assert.Equal(first, second);
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

    private static LocalModelCacheRecord[] BuildGoldenRecords() =>
    [
        new(
            "example/model",
            "D:/models/example",
            "main",
            new string('a', 64),
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero),
            IntegrityFailed: false,
            [BuildGoldenVariant()]),
        new(
            "example/model-b",
            "D:/models/example-b",
            "rev-2",
            new string('b', 64),
            new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero)),
    ];

    private static LocalModelVariantRecord BuildGoldenVariant() => new(
        "olive-cuda-fp16",
        "D:/models/example/optimized/olive-cuda-fp16",
        "encoder.onnx",
        ["encoder.onnx", "decoder.onnx"],
        "olive",
        ExecutionProviderKind.Cuda,
        "fp16",
        new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero),
        "main",
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

    private static string NormalizeNewLines(string value) => value.Replace("\r\n", "\n", StringComparison.Ordinal);

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
