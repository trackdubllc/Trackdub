using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Composition.Runtime;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class ModelDownloadOrchestratorTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.ModelDownloadOrchestrator.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadAsync_rejects_path_traversal_in_download_file()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistryWithMaliciousDownloadPath();
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Contains("segment", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(downloader.Destinations);
    }

    [Fact]
    public async Task DownloadAsync_downloads_to_configured_cache_when_manifest_root_is_outside_cache()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, string manifestRoot) =
            CreateRegistryWithManifestRootOutsideConfiguredCache();
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        string destination = Assert.Single(downloader.Destinations);
        Assert.StartsWith(
            Path.GetFullPath(storagePaths.ModelCacheDirectory),
            Path.GetFullPath(destination),
            StringComparison.OrdinalIgnoreCase);
        Assert.False(
            Path.GetFullPath(destination).StartsWith(Path.GetFullPath(manifestRoot), StringComparison.OrdinalIgnoreCase),
            $"Destination should not be under manifest root: {destination}");

        LocalModelCacheRecord record = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal(Path.Combine(storagePaths.ModelCacheDirectory, "example", "model"), record.RootPath);
    }

    [Fact]
    public async Task DownloadAsync_resolves_model_by_alias_and_keys_records_by_canonical_id()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache();
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        // "example" is the declared alias of "example/model"; the download must resolve
        // via the alias and key all state/records by the canonical model id.
        ModelDownloadResult result = await orchestrator.DownloadAsync(
            "example",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal("example/model", result.ModelId);
        LocalModelCacheRecord record = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
        Assert.Equal("example/model", record.ModelId);
    }

    [Fact]
    public async Task DownloadAsync_current_qwen3_asr_manifest_downloads_component_files()
    {
        BundledModelManifestRegistry registry = LoadBundledRegistryWithFakeDownloadHash(
            "tonythethompson/qwen3-asr-0.6b-onnx",
            "encoder.onnx");
        TrackdubStoragePaths storagePaths = new(tempRoot);
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths,
            hashVerifier: new NullModelHashVerifier());

        // Run the download; the result may be Corrupt if the bundled manifest now carries
        // real SHA256 hashes (synthetic downloader content won't match). The purpose of this
        // test is to verify that the orchestrator resolves the correct set of component files
        // from the real manifest — not to verify hash integrity with fake content.
        await orchestrator.DownloadAsync(
            "tonythethompson/qwen3-asr-0.6b-onnx",
            cancellationToken: TestContext.Current.CancellationToken);

        string modelRoot = Path.Combine(
            storagePaths.ModelCacheDirectory,
            "tonythethompson",
            "qwen3-asr-0.6b-onnx");
        string[] downloadedFiles = downloader.Destinations
            .Select(destination => Path.GetRelativePath(modelRoot, destination).Replace('\\', '/'))
            .ToArray();

        Assert.DoesNotContain("model.onnx", downloadedFiles, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(
            [
                "added_tokens.json",
                "config.json",
                "decoder_init.onnx",
                "decoder_step.onnx",
                "decoder_weights.data",
                "embed_tokens.bin",
                "preprocessor_config.json",
                "tokenizer.json",
                "tokenizer_config.json",
                "vocab.json",
                "encoder.onnx"
            ],
            downloadedFiles);
        Assert.Equal(downloadedFiles.Length, downloader.HubDownloads.Count + downloader.UriDownloads.Count);
        Assert.All(
            downloader.Destinations,
            destination => Assert.StartsWith(
                Path.GetFullPath(storagePaths.ModelCacheDirectory),
                Path.GetFullPath(destination),
                StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task DownloadAsync_includes_default_entry_when_manifest_lists_sidecar_files()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache("[ \"model.onnx.data\" ]");
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(2, downloader.Destinations.Count);
        Assert.Contains(
            Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "model.onnx.data"),
            downloader.Destinations);
        Assert.Contains(
            Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "onnx", "model.onnx"),
            downloader.Destinations);
    }

    [Fact]
    public async Task DownloadAsync_verifies_each_required_download_file_hash()
    {
        string sidecarHash = Sha256Hex("downloaded:example/model:sidecar.txt");
        string entryHash = Sha256Hex("downloaded:example/model:onnx/model.onnx");
        string hashesJson =
            $$"""
              "download_file_hashes": {
                "sidecar.txt": "{{sidecarHash}}",
                "onnx/model.onnx": "{{entryHash}}"
              },
              "hash_verification": {
                "mode": "required",
                "algorithm": "SHA-256"
              },
            """;
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"sidecar.txt\" ]",
                downloadFileHashesJson: hashesJson);
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(ModelCacheState.Installed, result.NewState);
    }

    [Fact]
    public async Task DownloadAsync_marks_corrupt_when_sidecar_hash_fails()
    {
        string entryHash = Sha256Hex("downloaded:example/model:onnx/model.onnx");
        string badSidecarHash = new('0', 64);
        string hashesJson =
            $$"""
              "download_file_hashes": {
                "sidecar.txt": "{{badSidecarHash}}",
                "onnx/model.onnx": "{{entryHash}}"
              },
              "hash_verification": {
                "mode": "required",
                "algorithm": "SHA-256"
              },
            """;
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"sidecar.txt\" ]",
                downloadFileHashesJson: hashesJson);
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ModelCacheState.Corrupt, result.NewState);
        Assert.Contains("sidecar.txt", result.FailureReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadAsync_uses_explicit_sources_for_entry_file_and_sidecars()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"model.onnx.data\" ]",
                downloadFileSourcesJson:
                """
                  "download_file_sources": {
                    "onnx/model.onnx": "https://example.test/models/model.onnx",
                    "model.onnx.data": "https://example.test/models/model.onnx.data"
                  },
                """);
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Empty(downloader.HubDownloads);
        Assert.Equal(
            [
                "https://example.test/models/model.onnx.data",
                "https://example.test/models/model.onnx"
            ],
            downloader.UriDownloads);
    }

    [Fact]
    public async Task DownloadAsync_does_not_generate_huggingface_download_for_cache_installed_entry()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(revision: "cache-installed");
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ModelCacheState.Missing, result.NewState);
        Assert.Contains("No downloadable source", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(downloader.HubDownloads);
        Assert.Empty(downloader.UriDownloads);
    }

    [Fact]
    public async Task DownloadAsync_includes_default_variant_entry_and_support_files()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"tokenizer.json\" ]",
                variantsJson:
                """
                  "variants": [
                    {
                      "alias": "default",
                      "entry_path": "onnx/decoder_model_merged.onnx",
                      "download_files": [ "onnx/decoder_model_merged.onnx.data" ]
                    }
                  ]
                """);
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(
            [
                "tokenizer.json",
                "onnx/decoder_model_merged.onnx.data",
                "onnx/decoder_model_merged.onnx",
                "onnx/model.onnx"
            ],
            downloader.HubDownloads);
    }

    [Fact]
    public async Task DownloadAsync_does_not_pull_model_lab_variant_files_for_base_download()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"genai_config.json\" ]",
                variantsJson:
                """
                  "variants": [
                    {
                      "alias": "trt-rtx-fp16",
                      "entry_path": "trt-rtx-fp16/encoder.onnx",
                      "download_files": [
                        "trt-rtx-fp16/audio_processor_config.json",
                        "trt-rtx-fp16/genai_config.json"
                      ]
                    }
                  ]
                """);
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(
            ["genai_config.json", "onnx/model.onnx"],
            downloader.HubDownloads);
    }

    [Fact]
    public async Task DownloadAsync_when_installed_fetches_only_delta_files_added_to_manifest()
    {
        // P2-12: manifest grew (new voice pack) after first install; only absent files are fetched.
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"tokenizer.json\", \"voices/af_bella.bin\" ]");
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();

        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string benchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        string tokenizerPath = Path.Combine(cacheRoot, "tokenizer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        await File.WriteAllTextAsync(benchmarkPath, "existing-benchmark", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tokenizerPath, "existing-tokenizer", TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [new LocalModelCacheRecord("example/model", cacheRoot, "main", "", DateTimeOffset.UtcNow)],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(ModelCacheState.Installed, result.NewState);
        string deltaFile = Assert.Single(downloader.HubDownloads);
        Assert.Equal("voices/af_bella.bin", deltaFile);
    }

    [Fact]
    public async Task DownloadAsync_when_installed_and_delta_file_has_no_source_marks_corrupt_not_installed()
    {
        // P2-12 / Codex review: delta failure must produce Corrupt so readiness checks block.
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"tokenizer.json\", \"voices/af_bella.bin\" ]",
                revision: "cache-installed"); // cache-installed → no HF source → no downloadable source
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();

        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string benchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        string tokenizerPath = Path.Combine(cacheRoot, "tokenizer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        await File.WriteAllTextAsync(benchmarkPath, "existing-benchmark", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tokenizerPath, "existing-tokenizer", TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [new LocalModelCacheRecord("example/model", cacheRoot, "main", "", DateTimeOffset.UtcNow)],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.Equal(ModelCacheState.Corrupt, result.NewState);
        Assert.Empty(downloader.HubDownloads);

        // Cache record must have IntegrityFailed=true so inventory/readiness sees Corrupt, not Installed.
        IReadOnlyList<LocalModelCacheRecord> records = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(records).IntegrityFailed);
    }

    [Fact]
    public async Task DownloadAsync_when_installed_and_delta_download_cancelled_marks_corrupt_not_installed()
    {
        // Codex P2 review: cancelling a delta download must not leave the model as Installed,
        // because the missing required files remain absent on disk.
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"tokenizer.json\", \"voices/af_bella.bin\" ]");
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CancellingDownloader();

        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string benchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        string tokenizerPath = Path.Combine(cacheRoot, "tokenizer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        await File.WriteAllTextAsync(benchmarkPath, "existing-benchmark", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tokenizerPath, "existing-tokenizer", TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [new LocalModelCacheRecord("example/model", cacheRoot, "main", "", DateTimeOffset.UtcNow)],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Equal(ModelCacheState.Corrupt, result.NewState);

        IReadOnlyList<LocalModelCacheRecord> records = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.True(Assert.Single(records).IntegrityFailed,
            "Cache record must be marked IntegrityFailed so inventory sees Corrupt, not Installed, after a cancelled delta.");
    }

    [Fact]
    public async Task DownloadAsync_when_missing_and_cancelled_returns_cancelled_result_without_throwing()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache();
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CancellingDownloader();
        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync(
            "example/model",
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.Success);
        Assert.True(result.Cancelled);
        Assert.Equal(ModelCacheState.Missing, result.NewState);
        Assert.Equal("Download cancelled.", result.FailureReason);
    }

    [Fact]
    public async Task DownloadAsync_when_installed_with_all_files_present_skips_download()
    {
        // P2-12: all manifest files already on disk → immediate Installed, no network calls.
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache();
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();

        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string benchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        await File.WriteAllTextAsync(benchmarkPath, "existing", TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [new LocalModelCacheRecord("example/model", cacheRoot, "main", "", DateTimeOffset.UtcNow)],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.DownloadAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(ModelCacheState.Installed, result.NewState);
        Assert.Empty(downloader.HubDownloads);
        Assert.Empty(downloader.UriDownloads);
    }

    [Fact]
    public async Task RepairAsync_preserves_optimized_variants_in_cache_index()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"tokenizer.json\", \"voices/af_bella.bin\" ]");
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();

        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string benchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        string tokenizerPath = Path.Combine(cacheRoot, "tokenizer.json");
        string variantRoot = Path.Combine(cacheRoot, "optimized", "dml");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        Directory.CreateDirectory(variantRoot);
        await File.WriteAllTextAsync(benchmarkPath, "existing", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tokenizerPath, "existing", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(Path.Combine(variantRoot, "model.onnx"), "optimized", TestContext.Current.CancellationToken);

        var existingVariant = new LocalModelVariantRecord(
            "dml-fp16",
            variantRoot,
            "model.onnx",
            ["model.onnx"],
            "olive",
            ExecutionProviderKind.DirectMl,
            "fp16",
            DateTimeOffset.UtcNow,
            "main");

        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/model",
                    cacheRoot,
                    "main",
                    "",
                    DateTimeOffset.UtcNow,
                    Variants: [existingVariant])
            ],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.RepairAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        LocalModelCacheRecord record = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
        LocalModelVariantRecord variant = Assert.Single(record.Variants);
        Assert.Equal("dml-fp16", variant.Alias);
    }

    [Fact]
    public async Task RepairAsync_when_installed_with_missing_files_downloads_only_missing_without_deleting_existing()
    {
        // P2-12: RepairAsync must not nuke existing files when only delta files are missing.
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache(
                downloadFilesJson: "[ \"tokenizer.json\", \"voices/af_bella.bin\" ]");
        var store = new LocalModelCacheRecordStore(storagePaths);
        var downloader = new CaptureDestinationDownloader();

        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string benchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        string tokenizerPath = Path.Combine(cacheRoot, "tokenizer.json");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        const string existingContent = "should-not-be-deleted";
        await File.WriteAllTextAsync(benchmarkPath, existingContent, TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(tokenizerPath, existingContent, TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [new LocalModelCacheRecord("example/model", cacheRoot, "main", "", DateTimeOffset.UtcNow)],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, downloader, storagePaths);

        ModelDownloadResult result = await orchestrator.RepairAsync("example/model", cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.Success, result.FailureReason);
        Assert.Equal(["voices/af_bella.bin"], downloader.HubDownloads);
        Assert.Equal(existingContent, await File.ReadAllTextAsync(benchmarkPath, TestContext.Current.CancellationToken));
        Assert.Equal(existingContent, await File.ReadAllTextAsync(tokenizerPath, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Inventory_uses_registered_cache_root_when_manifest_root_is_outside_cache()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithManifestRootOutsideConfiguredCache();
        var store = new LocalModelCacheRecordStore(storagePaths);
        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string cachedModelPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedModelPath)!);
        await File.WriteAllTextAsync(cachedModelPath, "cached", TestContext.Current.CancellationToken);
        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/model",
                    cacheRoot,
                    "main",
                    "",
                    DateTimeOffset.UtcNow)
            ],
            TestContext.Current.CancellationToken);
        var inventory = new ModelInventoryService(registry, store, storagePaths);

        ModelInventoryEntry? entry = await inventory.GetByModelIdAsync("example/model", TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal(ModelCacheState.Installed, entry!.State);
        Assert.Null(entry.FailureReason);
        Assert.Equal(new FileInfo(cachedModelPath).Length, entry.FileSizeBytes);
    }

    [Fact]
    public async Task VerifyAsync_marks_integrity_failed_so_inventory_stays_corrupt_not_missing()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithKnownSha256("0000000000000000000000000000000000000000000000000000000000000000");
        var store = new LocalModelCacheRecordStore(storagePaths);
        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string cachedBenchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedBenchmarkPath)!);
        await File.WriteAllTextAsync(cachedBenchmarkPath, "wrong-content", TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/model",
                    cacheRoot,
                    "main",
                    "0000000000000000000000000000000000000000000000000000000000000000",
                    DateTimeOffset.UtcNow,
                    IntegrityFailed: false)
            ],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, new StubDownloader(), storagePaths);
        ModelVerificationResult verification = await orchestrator.VerifyAsync("example/model", TestContext.Current.CancellationToken);

        Assert.False(verification.HashMatch);
        Assert.Equal(ModelCacheState.Corrupt, verification.NewState);

        IReadOnlyList<LocalModelCacheRecord> records = await store.LoadAsync(TestContext.Current.CancellationToken);
        LocalModelCacheRecord record = Assert.Single(records);
        Assert.True(record.IntegrityFailed);

        var inventory = new ModelInventoryService(registry, store, storagePaths);
        ModelInventoryEntry? entry = await inventory.GetByModelIdAsync("example/model", TestContext.Current.CancellationToken);
        Assert.NotNull(entry);
        Assert.Equal(ModelCacheState.Corrupt, entry!.State);
    }

    [Fact]
    public async Task VerifyAsync_accepts_bundled_manifest_root_outside_configured_cache()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, string manifestRoot) =
            CreateRegistryWithManifestRootOutsideConfiguredCache();
        string benchmarkPath = Path.Combine(manifestRoot, "onnx", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(benchmarkPath)!);
        await File.WriteAllTextAsync(benchmarkPath, "bundled-model", TestContext.Current.CancellationToken);

        var store = new LocalModelCacheRecordStore(storagePaths);
        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/model",
                    manifestRoot,
                    "main",
                    "",
                    DateTimeOffset.UtcNow)
            ],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, new StubDownloader(), storagePaths);
        ModelVerificationResult verification = await orchestrator.VerifyAsync("example/model", TestContext.Current.CancellationToken);

        Assert.True(verification.HashMatch);
        Assert.Equal(ModelCacheState.Installed, verification.NewState);
    }

    [Fact]
    public async Task DownloadAsync_emits_installed_then_verify_can_clear_integrity_flag()
    {
        const string expectedSha = "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824";
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths, _) =
            CreateRegistryWithKnownSha256(expectedSha);
        var store = new LocalModelCacheRecordStore(storagePaths);
        string cacheRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string cachedBenchmarkPath = Path.Combine(cacheRoot, "onnx", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(cachedBenchmarkPath)!);
        await File.WriteAllTextAsync(cachedBenchmarkPath, "hello", TestContext.Current.CancellationToken);
        // tokenizer.json is also a required download file in the manifest; must exist for verification.
        await File.WriteAllTextAsync(Path.Combine(cacheRoot, "tokenizer.json"), "{}", TestContext.Current.CancellationToken);

        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/model",
                    cacheRoot,
                    "main",
                    expectedSha,
                    DateTimeOffset.UtcNow,
                    IntegrityFailed: true)
            ],
            TestContext.Current.CancellationToken);

        var orchestrator = new ModelDownloadOrchestrator(registry, store, new StubDownloader(), storagePaths);
        ModelVerificationResult verification = await orchestrator.VerifyAsync("example/model", TestContext.Current.CancellationToken);

        Assert.True(verification.HashMatch);
        IReadOnlyList<LocalModelCacheRecord> records = await store.LoadAsync(TestContext.Current.CancellationToken);
        Assert.False(Assert.Single(records).IntegrityFailed);
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
        catch
        {
            // best-effort cleanup
        }
    }

    private (BundledModelManifestRegistry Registry, TrackdubStoragePaths StoragePaths) CreateRegistryWithMaliciousDownloadPath()
    {
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string manifestPath = Path.Combine(storagePaths.ModelCacheDirectory, "_orch", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.Combine(storagePaths.ModelCacheDirectory, "example-model"));
        File.WriteAllText(
            manifestPath,
            """
            {
              "models": [
                {
                  "model_id": "example/model",
                  "task": "tts",
                  "engine_family": "example-tts",
                  "capabilities": [ "tts" ],
                  "language_coverage": {
                    "target_languages": [ "en" ]
                  },
                  "tier": "balanced",
                  "license": "MIT",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": false,
                  "source_url": "https://huggingface.co/example/model",
                  "revision": "main",
                  "sha256": "",
                  "aliases": [ "example" ],
                  "root_path": "../example-model",
                  "benchmark_entry": "onnx/model.onnx",
                  "download_files": [ "../outside/token.json" ],
                  "variants": [
                    {
                      "alias": "q4",
                      "entry_path": "onnx/model_q4.onnx",
                      "download_files": [ "onnx/model_q4.onnx_data" ]
                    }
                  ]
                }
              ]
            }
            """);

        return (BundledModelManifestRegistry.Load(manifestPath), storagePaths);
    }

    private (BundledModelManifestRegistry Registry, TrackdubStoragePaths StoragePaths, string ManifestRoot)
        CreateRegistryWithManifestRootOutsideConfiguredCache(
            string downloadFilesJson = "[ \"onnx/model.onnx\" ]",
            string downloadFileSourcesJson = "",
            string downloadFileHashesJson = "",
            string revision = "main",
            string variantsJson = "\"variants\": []")
    {
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string manifestDirectory = Path.Combine(tempRoot, "manifest-outside-cache");
        string manifestPath = Path.Combine(manifestDirectory, "manifest.json");
        string manifestRoot = Path.Combine(tempRoot, "repo-models", "example-model");
        Directory.CreateDirectory(manifestDirectory);
        Directory.CreateDirectory(manifestRoot);
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "models": [
                {
                  "model_id": "example/model",
                  "task": "tts",
                  "engine_family": "example-tts",
                  "capabilities": [ "tts" ],
                  "language_coverage": {
                    "target_languages": [ "en" ]
                  },
                  "tier": "balanced",
                  "license": "MIT",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": false,
                  "source_url": "https://huggingface.co/example/model",
                  "revision": "{{revision}}",
                  "sha256": "",
                  "aliases": [ "example" ],
                  "root_path": "{{manifestRoot.Replace("\\", "\\\\")}}",
                  "benchmark_entry": "onnx/model.onnx",
                  "download_files": {{downloadFilesJson}},
            {{downloadFileSourcesJson}}{{downloadFileHashesJson}}      {{variantsJson}}
                }
              ]
            }
            """);

        return (BundledModelManifestRegistry.Load(manifestPath), storagePaths, manifestRoot);
    }

    private static string Sha256Hex(string value) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();

    private (BundledModelManifestRegistry Registry, TrackdubStoragePaths StoragePaths, string BenchmarkPath) CreateRegistryWithKnownSha256(string sha256)
    {
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string manifestPath = Path.Combine(storagePaths.ModelCacheDirectory, "_orch", "manifest-sha.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.Combine(storagePaths.ModelCacheDirectory, "example-model"));
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "models": [
                {
                  "model_id": "example/model",
                  "task": "tts",
                  "engine_family": "example-tts",
                  "capabilities": [ "tts" ],
                  "language_coverage": {
                    "target_languages": [ "en" ]
                  },
                  "tier": "balanced",
                  "license": "MIT",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": true,
                  "source_url": "https://huggingface.co/example/model",
                  "revision": "main",
                  "sha256": "{{sha256}}",
                  "aliases": [ "example" ],
                  "root_path": "../example-model",
                  "benchmark_entry": "onnx/model.onnx",
                  "download_files": [ "tokenizer.json" ],
                  "variants": [
                    {
                      "alias": "q4",
                      "entry_path": "onnx/model_q4.onnx",
                      "download_files": [ "onnx/model_q4.onnx_data" ]
                    }
                  ]
                }
              ]
            }
            """);

        BundledModelManifestRegistry registry = BundledModelManifestRegistry.Load(manifestPath);
        BundledModelManifestEntry entry = registry.Entries.Single(e => e.ModelId == "example/model");
        return (registry, storagePaths, entry.DefaultBenchmarkEntryPath);
    }

    private static BundledModelManifestRegistry LoadBundledRegistry()
    {
        string repoRoot = FindRepoRoot();
        string manifestPath = Path.Combine(
            repoRoot,
            "src",
            "Trackdub.Inference",
            "Runtime",
            "ModelManifest",
            "bundled-models.manifest.json");
        return BundledModelManifestRegistry.Load(manifestPath);
    }

    private BundledModelManifestRegistry LoadBundledRegistryWithFakeDownloadHash(string modelId, string benchmarkEntry)
    {
        string repoRoot = FindRepoRoot();
        string sourceManifestPath = Path.Combine(
            repoRoot,
            "src",
            "Trackdub.Inference",
            "Runtime",
            "ModelManifest",
            "bundled-models.manifest.json");
        string testManifestPath = Path.Combine(tempRoot, "_bundled", "bundled-models.manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(testManifestPath)!);

        JsonObject root = JsonNode.Parse(File.ReadAllText(sourceManifestPath))!.AsObject();
        JsonArray models = root["models"]!.AsArray();
        JsonObject model = models
            .Select(node => node!.AsObject())
            .Single(candidate => string.Equals(
                candidate["model_id"]!.GetValue<string>(),
                modelId,
                StringComparison.OrdinalIgnoreCase));

        // The production manifest pins the real benchmark-entry hash; this test's downloader writes fake bytes.
        model["sha256"] = Sha256Hex($"downloaded:{modelId}:{benchmarkEntry}");
        File.WriteAllText(testManifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));

        return BundledModelManifestRegistry.Load(testManifestPath);
    }

    private static string FindRepoRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(
                directory.FullName,
                "src",
                "Trackdub.Inference",
                "Runtime",
                "ModelManifest",
                "bundled-models.manifest.json");
            if (File.Exists(candidate))
                return directory.FullName;

            directory = directory.Parent;
        }

        throw new FileNotFoundException("Could not locate bundled-models.manifest.json from the test output directory.");
    }

    private sealed class CaptureDestinationDownloader : IModelDownloaderContract
    {
        public List<string> Destinations { get; } = [];
        public List<string> HubDownloads { get; } = [];
        public List<string> UriDownloads { get; } = [];

        public Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null)
        {
            HubDownloads.Add(fileName.Replace('\\', '/'));
            Destinations.Add(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllText(destinationPath, $"downloaded:{modelId}:{fileName}");
            progress?.Report(new ModelDownloadProgress(1, 1, 100, null));
            return Task.FromResult(true);
        }

        public Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            UriDownloads.Add(sourceUri.AbsoluteUri);
            Destinations.Add(destinationPath);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            File.WriteAllText(destinationPath, $"downloaded:{sourceUri}");
            progress?.Report(new ModelDownloadProgress(1, 1, 100, null));
            return Task.FromResult(true);
        }

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || string.IsNullOrWhiteSpace(expectedHash) || !File.Exists(filePath))
                return Task.FromResult(false);

            cancellationToken.ThrowIfCancellationRequested();

            byte[] bytes = File.ReadAllBytes(filePath);
            byte[] hashBytes = SHA256.HashData(bytes);
            string actualHex = Convert.ToHexString(hashBytes).ToLowerInvariant();

            string normalizedExpected = expectedHash.Trim().ToLowerInvariant();
            if (normalizedExpected.StartsWith("sha256:"))
                normalizedExpected = normalizedExpected["sha256:".Length..];

            return Task.FromResult(actualHex == normalizedExpected);
        }
    }

    private sealed class CancellingDownloader : IModelDownloaderContract
    {
        public Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null) => throw new OperationCanceledException("cancelled by test");

        public Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => throw new OperationCanceledException("cancelled by test");

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class StubDownloader : IModelDownloaderContract
    {
        public Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null) => Task.FromResult(true);

        public Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default) => Task.FromResult(true);

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }
}
