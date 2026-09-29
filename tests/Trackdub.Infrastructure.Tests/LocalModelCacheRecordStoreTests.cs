using System.Text;
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

    // Byte-identical round trip anchored on the frozen wire format rather than on whatever this
    // build's writer happens to emit: the exact bytes a stored index must have are placed on disk,
    // read back through LoadAsync, and written out again by SaveAsync. A read-path change that
    // drops, renames, reorders or reshapes a field fails here even if reader and writer were moved
    // together, so the format loaded today is the format that stays on disk.
    [Fact]
    public async Task Index_bytes_round_trip_byte_for_byte_from_the_frozen_wire_format()
    {
        LocalModelCacheRecordStore store = CreateStore();
        TrackdubStoragePaths storagePaths = CreateStoragePaths();
        Directory.CreateDirectory(storagePaths.ModelCacheDirectory);
        // Indented output follows the platform newline (JsonWriterOptions.NewLine defaults to it),
        // so the frozen sample is materialised with the same convention; the comparison below is
        // then verbatim, with no byte normalised on either side.
        Assert.Equal(Environment.NewLine, new JsonWriterOptions().NewLine);
        byte[] frozen = Encoding.UTF8.GetBytes(ExpectedIndexJson.Replace("\n", Environment.NewLine, StringComparison.Ordinal));
        await File.WriteAllBytesAsync(
            storagePaths.ModelCacheIndexPath,
            frozen,
            TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        // The reader has to recover the whole graph, not merely tolerate the bytes.
        Assert.Equal(["example/model", "example/model-b"], loaded.Select(record => record.ModelId));
        LocalModelVariantRecord variant = Assert.Single(loaded[0].Variants);
        Assert.Equal("olive-cuda-fp16", variant.Alias);
        Assert.Equal(ExecutionProviderKind.Cuda, variant.ExecutionProvider);
        ModelOptimizedVariantProvenance provenance = Assert.IsType<ModelOptimizedVariantProvenance>(variant.Provenance);
        Assert.Equal(
            [ModelOptimizationOperation.Compression, ModelOptimizationOperation.Registration],
            provenance.Operations);

        await store.SaveAsync(loaded, TestContext.Current.CancellationToken);

        byte[] rewritten = await File.ReadAllBytesAsync(
            storagePaths.ModelCacheIndexPath,
            TestContext.Current.CancellationToken);

        Assert.Equal(frozen, rewritten);
    }

    // Every shape this path can meet on disk has to load: CRLF and LF newline conventions (this
    // build writes the platform one, another OS or a script writes the other), a UTF-8 byte order
    // mark, which Notepad and PowerShell -Encoding utf8 produce, and a compact body with no
    // whitespace at all. Whatever comes in, the store normalizes back to the pinned bytes on save,
    // so the restored tolerance cannot start drifting the file.
    [Theory]
    [InlineData("crlf")]
    [InlineData("lf")]
    [InlineData("bom")]
    [InlineData("compact")]
    public async Task LoadAsync_accepts_every_on_disk_index_shape(string shape)
    {
        LocalModelCacheRecordStore store = CreateStore();
        TrackdubStoragePaths storagePaths = CreateStoragePaths();
        Directory.CreateDirectory(storagePaths.ModelCacheDirectory);
        await File.WriteAllBytesAsync(
            storagePaths.ModelCacheIndexPath,
            BuildIndexPayload(shape),
            TestContext.Current.CancellationToken);

        IReadOnlyList<LocalModelCacheRecord> loaded = await store.LoadAsync(TestContext.Current.CancellationToken);

        Assert.Equal(["example/model", "example/model-b"], loaded.Select(record => record.ModelId));
        Assert.Equal("olive-cuda-fp16", Assert.Single(loaded[0].Variants).Alias);
        Assert.Equal(
            [ModelOptimizationOperation.Compression, ModelOptimizationOperation.Registration],
            Assert.IsType<ModelOptimizedVariantProvenance>(loaded[0].Variants[0].Provenance).Operations);

        await store.SaveAsync(loaded, TestContext.Current.CancellationToken);

        Assert.Equal(PinnedIndexBytes(), await File.ReadAllBytesAsync(
            storagePaths.ModelCacheIndexPath,
            TestContext.Current.CancellationToken));
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

    // The pinned sample with the newline convention the store's indented writer emits on this OS.
    private static byte[] PinnedIndexBytes() =>
        Encoding.UTF8.GetBytes(ExpectedIndexJson.Replace("\n", Environment.NewLine, StringComparison.Ordinal));

    private static byte[] BuildIndexPayload(string shape) => shape switch
    {
        // Windows convention, including when read on Linux or macOS.
        "crlf" => Encoding.UTF8.GetBytes(ExpectedIndexJson.Replace("\n", "\r\n", StringComparison.Ordinal)),
        // Linux/macOS convention, including when read on Windows.
        "lf" => Encoding.UTF8.GetBytes(ExpectedIndexJson),
        // A byte order mark in front of the bytes this OS's writer would produce.
        "bom" => [0xEF, 0xBB, 0xBF, .. PinnedIndexBytes()],
        // A minimal writer that drops every whitespace character.
        "compact" => JsonSerializer.SerializeToUtf8Bytes(
            BuildGoldenRecords(),
            new JsonSerializerOptions { WriteIndented = false }),
        _ => throw new ArgumentOutOfRangeException(nameof(shape), shape, "Unsupported index shape."),
    };

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
