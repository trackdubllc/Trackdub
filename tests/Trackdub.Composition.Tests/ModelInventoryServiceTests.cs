using Trackdub.Composition.Runtime;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class ModelInventoryServiceTests : IDisposable
{
    private readonly string tempRoot = Path.Combine(
        Path.GetTempPath(),
        "Trackdub.ModelInventoryService.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GetByModelIdAsync_includes_expected_runtime_hint_from_manifest()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "source_languages": [ "en" ],
              "target_languages": [ "es" ]
            },
            """,
            extraModelJson: """
            "expected_runtime": "windows-ml|onnxruntime-migraphx|onnxruntime-directml",
            """);
        var service = new ModelInventoryService(registry, new LocalModelCacheRecordStore(storagePaths), storagePaths);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal(ModelExpectedRuntime.WindowsMlCatalogOrMigraphxOrDirectMl, entry!.ExpectedRuntime);
        Assert.Equal(
            ModelExpectedRuntimeFormatter.FormatHint(ModelExpectedRuntime.WindowsMlCatalogOrMigraphxOrDirectMl),
            entry.ExpectedRuntimeHint);
    }

    [Fact]
    public async Task GetByModelIdAsync_labels_direct_translation_pair_as_only_that_pair()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "es" }
              ]
            },
            """);
        var service = new ModelInventoryService(registry, new LocalModelCacheRecordStore(storagePaths), storagePaths);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal("Language scope: English -> Spanish only", entry!.LanguageCoverageDisplay);
    }

    [Fact]
    public async Task GetByModelIdAsync_labels_shared_source_translation_pairs_with_targets()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "pt" },
                { "source": "en", "target": "fr" },
                { "source": "en", "target": "es" },
                { "source": "en", "target": "it" },
                { "source": "en", "target": "ro" }
              ]
            },
            """);
        var service = new ModelInventoryService(registry, new LocalModelCacheRecordStore(storagePaths), storagePaths);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal(
            "Language scope: English -> Portuguese, French, Spanish, Italian, Romanian only",
            entry!.LanguageCoverageDisplay);
    }

    [Fact]
    public async Task GetByModelIdAsync_labels_madlad_translation_as_multilingual()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "source_languages": [ "multi" ],
              "target_languages": [ "multi" ]
            },
            """,
            engineFamily: "madlad");
        var service = new ModelInventoryService(registry, new LocalModelCacheRecordStore(storagePaths), storagePaths);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.Equal("Language scope: Multilingual", entry!.LanguageCoverageDisplay);
    }

    [Fact]
    public async Task GetByModelIdAsync_reports_cache_installed_model_as_not_auto_downloadable()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            revision: "cache-installed");
        var service = new ModelInventoryService(registry, new LocalModelCacheRecordStore(storagePaths), storagePaths);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.False(entry!.CanAutoDownload);
        Assert.Equal(ModelCacheState.Missing, entry.State);
        Assert.Contains("No downloadable source", entry.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetByModelIdAsync_does_not_enable_optimization_for_legacy_bool_only()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            extraModelJson:
            """
                  "olive_optimizable": true,
            """);
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "model.onnx");
        var service = new ModelInventoryService(registry, store, storagePaths, CpuOnlyRuntime());

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.False(entry!.OptimizationAvailability!.HasProfile);
        Assert.False(entry.OptimizationAvailability.CanOptimize);
        Assert.False(entry.IsOliveOptimizable);
    }

    [Fact]
    public async Task GetByModelIdAsync_does_not_enable_optimization_from_onnx_benchmark_alone()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """);
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "model.onnx");
        var service = new ModelInventoryService(registry, store, storagePaths, CpuOnlyRuntime());

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry);
        Assert.False(entry!.OptimizationAvailability!.HasProfile);
        Assert.False(entry.OptimizationAvailability.CanOptimize);
        Assert.False(entry.IsOliveOptimizable);
    }

    [Fact]
    public async Task GetByModelIdAsync_enables_declared_nested_onnx_component_when_provider_available()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            benchmarkEntry: "nested/model.onnx",
            extraModelJson:
            """
                  "optimization": {
                    "olive": {
                      "mode": "existing-onnx-components",
                      "components": [ "nested/model.onnx" ],
                      "supported_providers": [ "cpu", "dml" ]
                    }
                  },
            """);
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "nested/model.onnx");
        var service = new ModelInventoryService(registry, store, storagePaths, RuntimeWithProviders(
            ExecutionProviderKind.Cpu,
            ExecutionProviderKind.DirectMl));

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry?.OptimizationAvailability);
        ModelOptimizationAvailability availability = entry!.OptimizationAvailability!;
        Assert.True(availability.HasProfile);
        Assert.True(availability.CanOptimize);
        Assert.Equal(["nested/model.onnx"], availability.ComponentRelativePaths);
        Assert.Equal("nested/model.onnx", availability.EntryRelativePath);
        Assert.Equal([ExecutionProviderKind.Cpu, ExecutionProviderKind.DirectMl], availability.AvailableProviders);
    }

    [Fact]
    public async Task GetByModelIdAsync_reports_missing_declared_component()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            extraModelJson:
            """
                  "optimization": {
                    "olive": {
                      "mode": "existing-onnx-components",
                      "components": [ "missing.onnx" ],
                      "supported_providers": [ "cpu" ]
                    }
                  },
            """);
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "model.onnx");
        var service = new ModelInventoryService(registry, store, storagePaths, CpuOnlyRuntime());

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry?.OptimizationAvailability);
        ModelOptimizationAvailability availability = entry!.OptimizationAvailability!;
        Assert.True(availability.HasProfile);
        Assert.False(availability.CanOptimize);
        Assert.Equal("Optimization component missing: missing.onnx.", availability.UnavailableReason);
    }

    [Fact]
    public async Task GetByModelIdAsync_intersects_profile_providers_with_runtime_capabilities()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            extraModelJson:
            """
                  "optimization": {
                    "olive": {
                      "mode": "existing-onnx-components",
                      "components": [ "model.onnx" ],
                      "supported_providers": [ "dml", "cuda" ]
                    }
                  },
            """);
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "model.onnx");
        var runtime = new FakeRuntimeSelectionService
        {
            Capabilities =
            [
                new ProviderCapability { Provider = ExecutionProviderKind.DirectMl, DeviceDetected = true },
                new ProviderCapability { Provider = ExecutionProviderKind.Cuda, DeviceDetected = false }
            ]
        };
        var service = new ModelInventoryService(registry, store, storagePaths, runtime);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry?.OptimizationAvailability);
        ModelOptimizationAvailability availability = entry!.OptimizationAvailability!;
        ExecutionProviderKind provider = Assert.Single(availability.AvailableProviders);
        Assert.Equal(ExecutionProviderKind.DirectMl, provider);
    }

    [Fact]
    public async Task GetByModelIdAsync_requires_loadable_gpu_provider_for_native_and_trt_rtx_optimization()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            extraModelJson:
            """
                  "optimization": {
                    "olive": {
                      "mode": "existing-onnx-components",
                      "components": [ "model.onnx" ],
                      "supported_providers": [ "cuda", "tensorrt", "trt-rtx", "cpu" ]
                    }
                  },
            """);
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "model.onnx");
        var runtime = new FakeRuntimeSelectionService
        {
            Capabilities =
            [
                new ProviderCapability { Provider = ExecutionProviderKind.Cpu, DeviceDetected = true, ProviderLoadable = true },
                new ProviderCapability { Provider = ExecutionProviderKind.Cuda, DeviceDetected = true, ProviderLoadable = false },
                new ProviderCapability { Provider = ExecutionProviderKind.TensorRt, DeviceDetected = true, ProviderLoadable = true },
                new ProviderCapability { Provider = ExecutionProviderKind.TensorRTRtx, DeviceDetected = true, ProviderLoadable = false }
            ]
        };
        var service = new ModelInventoryService(registry, store, storagePaths, runtime);

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        Assert.NotNull(entry?.OptimizationAvailability);
        Assert.Equal(
            [ExecutionProviderKind.TensorRt, ExecutionProviderKind.Cpu],
            entry!.OptimizationAvailability!.AvailableProviders);
    }

    [Fact]
    public async Task GetByModelIdAsync_projects_registered_optimized_variants()
    {
        (BundledModelManifestRegistry registry, TrackdubStoragePaths storagePaths) = CreateRegistry(
            """
            "language_coverage": {
              "language_pairs": [
                { "source": "en", "target": "fr" }
              ]
            },
            """,
            benchmarkEntry: "nested/model.onnx");
        LocalModelCacheRecordStore store = await InstallModelAsync(storagePaths, "nested/model.onnx");
        LocalModelCacheRecord cacheRecord = Assert.Single(await store.LoadAsync(TestContext.Current.CancellationToken));
        string variantRoot = Path.Combine(cacheRecord.RootPath, "optimized", "olive-cpu-fp32");
        string variantModelPath = Path.Combine(variantRoot, "nested", "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(variantModelPath)!);
        await File.WriteAllTextAsync(variantModelPath, "optimized", TestContext.Current.CancellationToken);
        DateTimeOffset createdAt = new(2026, 1, 2, 3, 4, 5, TimeSpan.Zero);
        await store.SaveAsync(
            [
                cacheRecord with
                {
                    Variants =
                    [
                        new LocalModelVariantRecord(
                            "olive-cpu-fp32",
                            variantRoot,
                            "nested/model.onnx",
                            [ "nested/model.onnx" ],
                            "olive",
                            ExecutionProviderKind.Cpu,
                            "fp32",
                            createdAt,
                            cacheRecord.Revision,
                            cacheRecord.Sha256)
                    ]
                }
            ],
            TestContext.Current.CancellationToken);
        var service = new ModelInventoryService(registry, store, storagePaths, RuntimeWithProviders(ExecutionProviderKind.Cpu));

        ModelInventoryEntry? entry = await service.GetByModelIdAsync(
            "example/translation-model",
            TestContext.Current.CancellationToken);

        ModelOptimizedVariantInfo variant = Assert.Single(entry!.OptimizedVariants);
        Assert.Equal("olive-cpu-fp32", variant.Alias);
        Assert.Equal("olive", variant.OptimizerId);
        Assert.Equal(ExecutionProviderKind.Cpu, variant.ExecutionProvider);
        Assert.Equal("fp32", variant.Precision);
        Assert.Equal(ModelCacheState.Ready, variant.State);
        Assert.Equal(createdAt, variant.CreatedAtUtc);
        Assert.Null(variant.FailureReason);
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

    private (BundledModelManifestRegistry Registry, TrackdubStoragePaths StoragePaths) CreateRegistry(
        string languageCoverageJson,
        string engineFamily = "opus-mt",
        string revision = "main",
        string benchmarkEntry = "model.onnx",
        string extraModelJson = "")
    {
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string manifestPath = Path.Combine(storagePaths.ModelCacheDirectory, "_inventory", "manifest.json");
        Directory.CreateDirectory(Path.GetDirectoryName(manifestPath)!);
        Directory.CreateDirectory(Path.Combine(storagePaths.ModelCacheDirectory, "example-model"));
        File.WriteAllText(
            manifestPath,
            $$"""
            {
              "models": [
                {
                  "model_id": "example/translation-model",
                  "task": "translation",
                  "engine_family": "{{engineFamily}}",
                  "capabilities": [ "translation" ],
                  {{languageCoverageJson}}
                  "tier": "fast",
                  "license": "MIT",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": false,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": false,
                  "source_url": "https://huggingface.co/example/translation-model",
                  "revision": "{{revision}}",
                  "sha256": "",
                  "aliases": [ "example-translation" ],
                  "root_path": "../example-model",
                  "benchmark_entry": "{{benchmarkEntry}}",
                  {{extraModelJson}}
                  "variants": []
                }
              ]
            }
            """);

        return (BundledModelManifestRegistry.Load(manifestPath), storagePaths);
    }

    private async Task<LocalModelCacheRecordStore> InstallModelAsync(
        TrackdubStoragePaths storagePaths,
        params string[] relativeFiles)
    {
        string rootPath = Path.Combine(storagePaths.ModelCacheDirectory, "example-model");
        Directory.CreateDirectory(rootPath);
        foreach (string relativeFile in relativeFiles)
        {
            string path = Path.Combine(rootPath, relativeFile.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, "onnx", TestContext.Current.CancellationToken);
        }

        var store = new LocalModelCacheRecordStore(storagePaths);
        await store.SaveAsync(
            [
                new LocalModelCacheRecord(
                    "example/translation-model",
                    rootPath,
                    "main",
                    "",
                    DateTimeOffset.UtcNow)
            ],
            TestContext.Current.CancellationToken);
        return store;
    }

    private static FakeRuntimeSelectionService CpuOnlyRuntime() =>
        RuntimeWithProviders(ExecutionProviderKind.Cpu);

    private static FakeRuntimeSelectionService RuntimeWithProviders(params ExecutionProviderKind[] providers) =>
        new()
        {
            Capabilities = providers
                .Select(provider => new ProviderCapability
                {
                    Provider = provider,
                    DeviceDetected = true
                })
                .ToList()
        };
}
