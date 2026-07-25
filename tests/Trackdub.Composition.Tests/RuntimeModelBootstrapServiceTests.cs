using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Composition.Runtime;
using Trackdub.Domain;
using Trackdub.Infrastructure.Settings;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Tests;

public sealed class RuntimeModelBootstrapServiceTests : IDisposable
{
    private const string ValidSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string MismatchedSha256 = "dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd";
    private readonly string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.RuntimeModelBootstrap.Tests", Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task DownloadRequiredModelAsync_downloads_manifest_package_files_and_registers_cache()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string expectedEntryPath = Path.Combine(
            storagePaths.ModelCacheDirectory,
            "example",
            "model",
            "onnx",
            "model_q4.onnx");
        var planner = new QueueRuntimePlanner(
            CreatePlan(StageRuntimePlanStatus.DownloadRequired),
            CreatePlan(StageRuntimePlanStatus.DownloadRequired),
            CreatePlan(StageRuntimePlanStatus.Ready, expectedEntryPath));
        var downloader = new RecordingModelDownloader();
        var registrar = new RecordingModelCacheRegistrar();
        var fingerprintService = new StaticFileFingerprintService();
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            downloader,
            registrar,
            fingerprintService,
            storagePaths);

        RequiredRuntimeModelStatus? missingStatus = await service.GetRequiredModelStatusAsync(
            new RuntimeModelRequest(RuntimeStage.Tts, "example", RequirePreferredModelAlias: true));

        Assert.NotNull(missingStatus);
        Assert.True(missingStatus!.CanAutoDownload);

        RequiredRuntimeModelStatus downloadedStatus = await service.DownloadRequiredModelAsync(
            new RuntimeModelRequest(RuntimeStage.Tts, "example", RequirePreferredModelAlias: true));

        Assert.True(downloadedStatus.IsAvailable);
        Assert.Equal(
            ["tokenizer.json", "onnx/model_q4.onnx_data", "onnx/model_q4.onnx"],
            downloader.DownloadedFiles);
        Assert.Equal(["main", "main", "main"], downloader.DownloadedRevisions);
        Assert.True(File.Exists(Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "tokenizer.json")));
        Assert.True(File.Exists(expectedEntryPath));
        Assert.NotNull(registrar.LastRecord);
        Assert.Equal("example/model", registrar.LastRecord!.ModelId);
        Assert.Equal(Path.Combine(storagePaths.ModelCacheDirectory, "example", "model"), registrar.LastRecord.RootPath);
    }

    [Fact]
    public async Task GetRequiredModelStatusAsync_ready_plan_reports_missing_support_files()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string modelRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string entryPath = Path.Combine(modelRoot, "onnx", "model_q4.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllBytes(entryPath, [1, 2, 3]);
        var planner = new QueueRuntimePlanner(CreatePlan(StageRuntimePlanStatus.Ready, entryPath));
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            new RecordingModelDownloader(),
            new RecordingModelCacheRegistrar(),
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus? status = await service.GetRequiredModelStatusAsync(
            new RuntimeModelRequest(RuntimeStage.Tts, "example", RequirePreferredModelAlias: true));

        Assert.NotNull(status);
        Assert.False(status!.IsAvailable);
        Assert.True(status.CanAutoDownload);
        Assert.Contains("support files", status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRequiredModelStatusAsync_selected_local_optimized_variant_is_not_downloadableAsync()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string variantRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "optimized", "olive-cpu-fp32");
        string entryPath = Path.Combine(variantRoot, "model.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllBytes(entryPath, [1, 2, 3]);
        var planner = new QueueRuntimePlanner(CreatePlan(StageRuntimePlanStatus.DownloadRequired, entryPath) with
        {
            Variant = "olive-cpu-fp32",
            IsLocalOptimizedVariant = true,
            ModelRootPath = variantRoot,
            ModelEntryRelativePath = "model.onnx",
            RequiredModelRelativePaths = ["model.onnx", "weights.onnx_data"],
            Fallback = new RuntimePlanFallback(
                RuntimePlanFallbackCode.ModelNotCached,
                "Selected optimized variant 'olive-cpu-fp32' is missing required local file 'weights.onnx_data'. Re-optimize the model or clear the variant selection.")
        });
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            new RecordingModelDownloader(),
            new RecordingModelCacheRegistrar(),
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus? status = await service.GetRequiredModelStatusAsync(
            new RuntimeModelRequest(
                RuntimeStage.Tts,
                "example",
                RequirePreferredModelAlias: true,
                PreferredModelVariantAlias: "olive-cpu-fp32"));

        Assert.NotNull(status);
        Assert.False(status!.IsAvailable);
        Assert.False(status.CanAutoDownload);
        Assert.False(status.CanImportSingleFile);
        Assert.Equal("weights.onnx_data", status.ExpectedFileName);
        Assert.Contains("Re-optimize", status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRequiredModelStatusAsync_selected_single_file_local_optimized_variant_is_not_importableAsync()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string variantRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "optimized", "olive-cpu-fp32");
        string entryPath = Path.Combine(variantRoot, "model.onnx");
        var planner = new QueueRuntimePlanner(CreatePlan(StageRuntimePlanStatus.DownloadRequired, entryPath) with
        {
            Variant = "olive-cpu-fp32",
            IsLocalOptimizedVariant = true,
            ModelRootPath = variantRoot,
            ModelEntryRelativePath = "model.onnx",
            RequiredModelRelativePaths = ["model.onnx"],
            Fallback = new RuntimePlanFallback(
                RuntimePlanFallbackCode.ModelNotCached,
                "Selected optimized variant 'olive-cpu-fp32' is missing required local file 'model.onnx'. Re-optimize the model or clear the variant selection.")
        });
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            new RecordingModelDownloader(),
            new RecordingModelCacheRegistrar(),
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus? status = await service.GetRequiredModelStatusAsync(
            new RuntimeModelRequest(
                RuntimeStage.Tts,
                "example",
                RequirePreferredModelAlias: true,
                PreferredModelVariantAlias: "olive-cpu-fp32"));

        Assert.NotNull(status);
        Assert.False(status!.IsAvailable);
        Assert.False(status.CanAutoDownload);
        Assert.False(status.CanImportSingleFile);
        Assert.Equal("model.onnx", status.ExpectedFileName);
        Assert.Contains("Re-optimize", status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task DownloadRequiredModelAsync_selected_single_file_local_optimized_variant_is_not_importableAsync()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string variantRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "optimized", "olive-cpu-fp32");
        string entryPath = Path.Combine(variantRoot, "model.onnx");
        var planner = new QueueRuntimePlanner(CreatePlan(StageRuntimePlanStatus.DownloadRequired, entryPath) with
        {
            Variant = "olive-cpu-fp32",
            IsLocalOptimizedVariant = true,
            ModelRootPath = variantRoot,
            ModelEntryRelativePath = "model.onnx",
            RequiredModelRelativePaths = ["model.onnx"],
            Fallback = new RuntimePlanFallback(
                RuntimePlanFallbackCode.ModelNotCached,
                "Selected optimized variant 'olive-cpu-fp32' is missing required local file 'model.onnx'. Re-optimize the model or clear the variant selection.")
        });
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            new RecordingModelDownloader(),
            new RecordingModelCacheRegistrar(),
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus status = await service.DownloadRequiredModelAsync(
            new RuntimeModelRequest(
                RuntimeStage.Tts,
                "example",
                RequirePreferredModelAlias: true,
                PreferredModelVariantAlias: "olive-cpu-fp32"));

        Assert.False(status.IsAvailable);
        Assert.False(status.CanAutoDownload);
        Assert.False(status.CanImportSingleFile);
        Assert.Equal("model.onnx", status.ExpectedFileName);
        Assert.Contains("Re-optimize", status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ImportRequiredModelAsync_selected_local_optimized_variant_is_not_importableAsync()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string variantRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model", "optimized", "olive-cpu-fp32");
        string entryPath = Path.Combine(variantRoot, "model.onnx");
        var planner = new QueueRuntimePlanner(CreatePlan(StageRuntimePlanStatus.DownloadRequired, entryPath) with
        {
            Variant = "olive-cpu-fp32",
            IsLocalOptimizedVariant = true,
            ModelRootPath = variantRoot,
            ModelEntryRelativePath = "model.onnx",
            RequiredModelRelativePaths = ["model.onnx"]
        });
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            new RecordingModelDownloader(),
            new RecordingModelCacheRegistrar(),
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus status = await service.ImportRequiredModelAsync(
            new RuntimeModelRequest(
                RuntimeStage.Tts,
                "example",
                RequirePreferredModelAlias: true,
                PreferredModelVariantAlias: "olive-cpu-fp32"),
            "ignored.onnx");

        Assert.False(status.IsAvailable);
        Assert.False(status.CanAutoDownload);
        Assert.False(status.CanImportSingleFile);
        Assert.Contains("local-only", status.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetRequiredModelStatusAsync_when_bundle_exists_without_cache_record_still_allows_auto_setup()
    {
        BundledModelManifestRegistry registry = CreateRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string modelRoot = Path.Combine(storagePaths.ModelCacheDirectory, "example", "model");
        string entryPath = Path.Combine(modelRoot, "onnx", "model_q4.onnx");
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllBytes(Path.Combine(modelRoot, "tokenizer.json"), [1]);
        File.WriteAllBytes(Path.Combine(modelRoot, "onnx", "model_q4.onnx_data"), [2]);
        File.WriteAllBytes(entryPath, [3]);
        var planner = new QueueRuntimePlanner(
            CreatePlan(StageRuntimePlanStatus.DownloadRequired),
            CreatePlan(StageRuntimePlanStatus.DownloadRequired),
            CreatePlan(StageRuntimePlanStatus.Ready, entryPath));
        var downloader = new RecordingModelDownloader();
        var registrar = new RecordingModelCacheRegistrar();
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            downloader,
            registrar,
            new StaticFileFingerprintService(),
            storagePaths);
        var request = new RuntimeModelRequest(RuntimeStage.Tts, "example", RequirePreferredModelAlias: true);

        RequiredRuntimeModelStatus? status = await service.GetRequiredModelStatusAsync(request);

        Assert.NotNull(status);
        Assert.False(status!.IsAvailable);
        Assert.True(status.CanAutoDownload);

        RequiredRuntimeModelStatus downloadedStatus = await service.DownloadRequiredModelAsync(request);

        Assert.True(downloadedStatus.IsAvailable);
        Assert.Empty(downloader.DownloadedFiles);
        Assert.Empty(downloader.DownloadedUris);
        Assert.NotNull(registrar.LastRecord);
        Assert.Equal("example/model", registrar.LastRecord!.ModelId);
        Assert.Equal(modelRoot, registrar.LastRecord.RootPath);
    }

    [Fact]
    public async Task DownloadRequiredModelAsync_for_hush_downloads_model_bundle_and_native_runtime()
    {
        BundledModelManifestRegistry registry = CreateHushRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string expectedEntryPath = Path.Combine(
            storagePaths.ModelCacheDirectory,
            "weya-ai",
            "hush",
            "onnx",
            "advanced_dfnet16k_model_best_onnx.tar.gz");
        var planner = new QueueRuntimePlanner(
            CreateHushPlan(StageRuntimePlanStatus.DownloadRequired),
            CreateHushPlan(StageRuntimePlanStatus.DownloadRequired),
            CreateHushPlan(StageRuntimePlanStatus.Ready, expectedEntryPath));
        var downloader = new RecordingModelDownloader();
        var registrar = new RecordingModelCacheRegistrar();
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            downloader,
            registrar,
            new StaticFileFingerprintService(),
            storagePaths);
        var request = new RuntimeModelRequest(
            RuntimeStage.Separation,
            PreferredModelAlias: "hush-dialogue",
            RequirePreferredModelAlias: true);

        RequiredRuntimeModelStatus? missingStatus = await service.GetRequiredModelStatusAsync(request);
        Assert.NotNull(missingStatus);
        Assert.True(missingStatus!.CanAutoDownload);
        Assert.Equal("Dialogue isolation", missingStatus.StageDisplayName);

        RequiredRuntimeModelStatus status = await service.DownloadRequiredModelAsync(request);

        Assert.True(status.IsAvailable);
        Assert.True(status.CanAutoDownload);
        Assert.Null(status.FailureReason);
        Assert.Equal(
            ["onnx/advanced_dfnet16k_model_best_onnx.tar.gz"],
            downloader.DownloadedFiles);
        Assert.Equal(
            ["https://example.test/hush/weya_nc.dll"],
            downloader.DownloadedUris);
        Assert.True(File.Exists(expectedEntryPath));
        Assert.True(File.Exists(Path.Combine(storagePaths.ModelCacheDirectory, "weya-ai", "hush", "deployment", "lib", "weya_nc.dll")));
        Assert.NotNull(registrar.LastRecord);
        Assert.Equal("weya-ai/hush", registrar.LastRecord!.ModelId);
    }

    [Fact]
    public async Task GetRequiredModelStatusAsync_for_hush_with_bundle_but_missing_native_runtime_still_allows_download()
    {
        BundledModelManifestRegistry registry = CreateHushRegistry();
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string entryPath = Path.Combine(
            storagePaths.ModelCacheDirectory,
            "weya-ai",
            "hush",
            "onnx",
            "advanced_dfnet16k_model_best_onnx.tar.gz");
        Directory.CreateDirectory(Path.GetDirectoryName(entryPath)!);
        File.WriteAllBytes(entryPath, [1]);
        var planner = new QueueRuntimePlanner(CreateHushPlan(StageRuntimePlanStatus.Ready, entryPath));
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            new RecordingModelDownloader(),
            new RecordingModelCacheRegistrar(),
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus? status = await service.GetRequiredModelStatusAsync(
            new RuntimeModelRequest(
                RuntimeStage.Separation,
                PreferredModelAlias: "hush-dialogue",
                RequirePreferredModelAlias: true));

        Assert.NotNull(status);
        Assert.False(status!.IsAvailable);
        Assert.True(status.CanAutoDownload);
        Assert.Contains("Download now", status.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("weya_nc.dll", status.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("deployment/lib/weya_nc.dll", status.ExpectedFileName);
        Assert.Equal(
            Path.Combine(storagePaths.ModelCacheDirectory, "weya-ai", "hush", "deployment", "lib", "weya_nc.dll"),
            status.ModelPath);
    }

    [Fact]
    public async Task DownloadRequiredModelAsync_rejects_file_when_sha256_does_not_match_manifest()
    {
        BundledModelManifestRegistry registry = CreateRegistryWithSha256(MismatchedSha256);
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string expectedEntryPath = Path.Combine(
            storagePaths.ModelCacheDirectory,
            "example", "model", "onnx", "model_q4.onnx");
        var planner = new QueueRuntimePlanner(
            CreatePlan(StageRuntimePlanStatus.DownloadRequired),
            CreatePlan(StageRuntimePlanStatus.DownloadRequired));
        var downloader = new RecordingModelDownloader();
        var registrar = new RecordingModelCacheRegistrar();
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            downloader,
            registrar,
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus result = await service.DownloadRequiredModelAsync(
            new RuntimeModelRequest(RuntimeStage.Tts, "example", RequirePreferredModelAlias: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.False(result.IsAvailable);
        Assert.Contains("hash", result.FailureReason, StringComparison.OrdinalIgnoreCase);
        Assert.Null(registrar.LastRecord);
        Assert.False(File.Exists(expectedEntryPath));
    }

    [Fact]
    public async Task DownloadRequiredModelAsync_accepts_file_when_sha256_matches_manifest()
    {
        // SHA-256 of the single byte [1] that RecordingModelDownloader writes
        const string knownSha256 = "4bf5122f344554c53bde2ebb8cd2b7e3d1600ad631c385a5d7cce23c7785459a";
        BundledModelManifestRegistry registry = CreateRegistryWithSha256(knownSha256);
        TrackdubStoragePaths storagePaths = new(tempRoot);
        string expectedEntryPath = Path.Combine(
            storagePaths.ModelCacheDirectory,
            "example", "model", "onnx", "model_q4.onnx");
        // Slot 1: DownloadRequiredModelAsync's planning call
        // Slot 2: post-download GetRequiredModelStatusAsync — Ready + files exist → returns null → isAvailable: true
        var planner = new QueueRuntimePlanner(
            CreatePlan(StageRuntimePlanStatus.DownloadRequired),
            CreatePlan(StageRuntimePlanStatus.Ready, expectedEntryPath));
        var downloader = new RecordingModelDownloader();
        var registrar = new RecordingModelCacheRegistrar();
        var service = new RuntimeModelBootstrapService(
            planner,
            registry,
            downloader,
            registrar,
            new StaticFileFingerprintService(),
            storagePaths);

        RequiredRuntimeModelStatus result = await service.DownloadRequiredModelAsync(
            new RuntimeModelRequest(RuntimeStage.Tts, "example", RequirePreferredModelAlias: true),
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.True(result.IsAvailable, $"FailureReason={result.FailureReason}");
        Assert.NotNull(registrar.LastRecord);
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
            // Best-effort test cleanup.
        }
    }

    private BundledModelManifestRegistry CreateRegistry()
    {
        string manifestPath = Path.Combine(tempRoot, "manifest.json");
        Directory.CreateDirectory(tempRoot);
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
                  "root_path": "models/example",
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

        return BundledModelManifestRegistry.Load(manifestPath);
    }

    private BundledModelManifestRegistry CreateRegistryWithSha256(string sha256)
    {
        string manifestPath = Path.Combine(tempRoot, "manifest-sha.json");
        Directory.CreateDirectory(tempRoot);
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
                  "root_path": "models/example",
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

        return BundledModelManifestRegistry.Load(manifestPath);
    }

    private BundledModelManifestRegistry CreateHushRegistry()
    {
        string manifestPath = Path.Combine(tempRoot, "hush-manifest.json");
        Directory.CreateDirectory(tempRoot);
        File.WriteAllText(
            manifestPath,
            """
            {
              "models": [
                {
                  "model_id": "weya-ai/hush",
                  "task": "separation",
                  "engine_family": "hush-dialogue",
                  "capabilities": [ "dialogue-ambiance-separation", "speech-enhancement", "background-speaker-suppression" ],
                  "language_coverage": {},
                  "tier": "quality",
                  "license": "Apache-2.0",
                  "commercial_allowed": true,
                  "redistribution_allowed": true,
                  "requires_attribution": true,
                  "requires_user_consent": false,
                  "voice_cloning": false,
                  "commercial_use_verified": false,
                  "source_url": "https://huggingface.co/weya-ai/hush",
                  "revision": "a55d932cbf6344d284ac985f21e7f6e5bc4d38a5",
                  "sha256": "",
                  "aliases": [ "hush-dialogue", "hush" ],
                  "root_path": "models/hush",
                  "benchmark_entry": "onnx/advanced_dfnet16k_model_best_onnx.tar.gz",
                  "download_files": [ "deployment/lib/weya_nc.dll" ],
                  "download_file_sources": {
                    "deployment/lib/weya_nc.dll": "https://example.test/hush/weya_nc.dll"
                  },
                  "variants": [
                    {
                      "alias": "default",
                      "entry_path": "onnx/advanced_dfnet16k_model_best_onnx.tar.gz"
                    }
                  ]
                }
              ]
            }
            """);

        return BundledModelManifestRegistry.Load(manifestPath);
    }

    private static StageRuntimePlan CreatePlan(
        StageRuntimePlanStatus status,
        string? entryPath = null) =>
        new()
        {
            Stage = RuntimeStage.Tts,
            Status = status,
            ModelId = "example/model",
            ModelAlias = "example",
            EngineFamily = "example-tts",
            Variant = "q4",
            ExecutionProvider = ExecutionProviderKind.Cpu,
            ModelEntryPath = entryPath,
            Fallback = status is StageRuntimePlanStatus.DownloadRequired
                ? new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "cache miss")
                : null
        };

    private static StageRuntimePlan CreateHushPlan(
        StageRuntimePlanStatus status,
        string? entryPath = null) =>
        new()
        {
            Stage = RuntimeStage.Separation,
            Status = status,
            ModelId = "weya-ai/hush",
            ModelAlias = "hush-dialogue",
            EngineFamily = "hush-dialogue",
            Variant = "default",
            ExecutionProvider = ExecutionProviderKind.Cpu,
            ModelEntryPath = entryPath,
            Fallback = status is StageRuntimePlanStatus.DownloadRequired
                ? new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "cache miss")
                : null
        };

    private sealed class QueueRuntimePlanner(params StageRuntimePlan[] plans) : IRuntimePlanner
    {
        private readonly Queue<StageRuntimePlan> plans = new(plans);

        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            if (plans.TryDequeue(out StageRuntimePlan? plan))
            {
                return Task.FromResult(plan);
            }

            throw new InvalidOperationException("No runtime plan was queued for the test.");
        }
    }

    private sealed class RecordingModelDownloader : IModelDownloaderContract
    {
        private readonly List<string> downloadedFiles = [];
        private readonly List<string?> downloadedRevisions = [];
        private readonly List<string> downloadedUris = [];

        public IReadOnlyList<string> DownloadedFiles => downloadedFiles;
        public IReadOnlyList<string?> DownloadedRevisions => downloadedRevisions;
        public IReadOnlyList<string> DownloadedUris => downloadedUris;

        public async Task<bool> DownloadAsync(
            string modelId,
            string fileName,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default,
            string? revision = null)
        {
            downloadedFiles.Add(fileName.Replace('\\', '/'));
            downloadedRevisions.Add(revision);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [1], cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelDownloadProgress(1, 1, 100, $"Downloaded {fileName}"));
            return true;
        }

        public async Task<bool> DownloadUriAsync(
            Uri sourceUri,
            string destinationPath,
            IProgress<ModelDownloadProgress>? progress = null,
            CancellationToken cancellationToken = default)
        {
            downloadedUris.Add(sourceUri.AbsoluteUri);
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [2], cancellationToken).ConfigureAwait(false);
            progress?.Report(new ModelDownloadProgress(1, 1, 100, $"Downloaded {sourceUri}"));
            return true;
        }

        public Task<bool> VerifyHashAsync(
            string filePath,
            string expectedHash,
            CancellationToken cancellationToken = default) => Task.FromResult(true);
    }

    private sealed class RecordingModelCacheRegistrar : IModelCacheRegistrar
    {
        public LocalModelCacheRecord? LastRecord { get; private set; }

        public Task RegisterAsync(
            LocalModelCacheRecord record,
            CancellationToken cancellationToken = default)
        {
            LastRecord = record;
            return Task.CompletedTask;
        }
    }

    private sealed class StaticFileFingerprintService : IFileFingerprintService
    {
        public Task<FileFingerprint> ComputeAsync(string path, CancellationToken cancellationToken) =>
            Task.FromResult(new FileFingerprint("hash", new FileInfo(path).Length, DateTimeOffset.UnixEpoch));
    }
}
