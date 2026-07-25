using System.Text.Json;
using System.Text.Json.Nodes;
using Trackdub.Composition.Runtime;
using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackCompatibilityServiceTests
{
    [Fact]
    public async Task EvaluateAsync_reports_not_runnable_when_required_models_missing()
    {
        StarterPackCompatibilityService service = CreateService();

        StarterPackCompatibilityReport report = await service.EvaluateAsync("basic", "default");

        Assert.Equal("basic", report.PackId);
        Assert.False(report.AllStagesRunnable);
        Assert.Equal("not_runnable", report.CompatibilityStatus);
        Assert.Contains(report.Stages, stage => stage.FallbackReason == "download_required");
    }

    [Fact]
    public async Task EvaluateAsync_includes_profile_selected_asr_stage()
    {
        StarterPackCompatibilityService service = CreateService();

        StarterPackCompatibilityReport report = await service.EvaluateAsync("basic", "default");

        Assert.Contains(
            report.Stages,
            stage => string.Equals(stage.Stage, StageNames.Asr, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task EvaluateAsync_marks_stage_not_runnable_when_execution_provider_not_ready()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = request => new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = StageRuntimePlanStatus.Blocked,
                Variant = request.PreferredModelVariantAlias,
                ExecutionProvider = request.PreferredExecutionProvider,
                Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ProviderUnavailable, "DirectML unavailable")
            }
        };
        StarterPackCompatibilityService service = CreateService(runtimePlanner: planner);

        StarterPackCompatibilityReport report = await service.EvaluateAsync("basic", "default");

        Assert.False(report.AllStagesRunnable);
        Assert.Contains(report.Stages, stage => !stage.Runnable && stage.FallbackReason == "blocked:provider_unavailable");
    }

    [Fact]
    public async Task EvaluateAsync_marks_verified_planner_result_runnable_for_starter_pack_readiness()
    {
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = request => new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = StageRuntimePlanStatus.Verified,
                Variant = request.PreferredModelVariantAlias,
                ExecutionProvider = request.PreferredExecutionProvider
            }
        };
        StarterPackCompatibilityService service = CreateService(runtimePlanner: planner);

        StarterPackCompatibilityReport report = await service.EvaluateAsync("basic", "default");

        Assert.True(report.AllStagesRunnable);
        Assert.All(report.Stages, stage => Assert.True(stage.Runnable));
    }

    [Fact]
    public async Task EvaluateAsync_plans_starter_pack_stage_with_model_variant_provider_triple()
    {
        StageRuntimePlanningRequest? capturedRequest = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = request =>
            {
                capturedRequest ??= request;
                return new StageRuntimePlan
                {
                    Stage = request.Stage,
                    Status = StageRuntimePlanStatus.Ready,
                    Variant = request.PreferredModelVariantAlias,
                    ExecutionProvider = request.PreferredExecutionProvider ?? ExecutionProviderKind.Cpu
                };
            }
        };
        StarterPackCompatibilityService service = CreateService(runtimePlanner: planner);

        await service.EvaluateAsync("basic", "default", StarterPackHardwareProfile.CpuSafe);

        Assert.NotNull(capturedRequest);
        Assert.Equal(RuntimeStage.Vad, capturedRequest.Stage);
        Assert.Equal("onnx-community/silero-vad", capturedRequest.PreferredModelAlias);
        Assert.True(capturedRequest.RequirePreferredModelAlias);
        Assert.Equal("int8", capturedRequest.PreferredModelVariantAlias);
        Assert.Equal(ExecutionProviderKind.Cpu, capturedRequest.PreferredExecutionProvider);
        Assert.True(capturedRequest.RequirePreferredExecutionProvider);
    }

    [Fact]
    public async Task EvaluateAsync_does_not_mark_auto_provider_selection_as_fallback()
    {
        StageRuntimePlanningRequest? capturedRequest = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = request =>
            {
                capturedRequest = request;
                return new StageRuntimePlan
                {
                    Stage = request.Stage,
                    Status = StageRuntimePlanStatus.Ready,
                    Variant = request.PreferredModelVariantAlias,
                    ExecutionProvider = ExecutionProviderKind.DirectMl
                };
            }
        };
        StarterPackCompatibilityService service = CreateService(
            runtimePlanner: planner,
            catalog: CreateCatalogWithUserPack("""
                {
                  "schema_version": 1,
                  "id": "auto-provider-pack",
                  "display_name": "Auto Provider Pack",
                  "tier_preference": "balanced",
                  "description": "Tests auto provider planning.",
                  "profiles": [{ "id": "default", "display_name": "Default" }],
                  "models": [
                    {
                      "model_id": "onnx-community/silero-vad",
                      "stage": "vad",
                      "required": true,
                      "alias": "silero-vad",
                      "runtime_defaults": {
                        "cpu_safe": {}
                      }
                    }
                  ]
                }
                """));

        StarterPackCompatibilityReport report = await service.EvaluateAsync(
            "auto-provider-pack",
            "default",
            StarterPackHardwareProfile.CpuSafe);

        StageCompatibilityEntry stage = Assert.Single(report.Stages);
        Assert.True(report.AllStagesRunnable);
        Assert.False(report.AnyFallbackApplied);
        Assert.False(stage.FallbackApplied);
        Assert.Equal("fully_compatible", report.CompatibilityStatus);
        Assert.NotNull(capturedRequest);
        Assert.Null(capturedRequest.PreferredExecutionProvider);
        Assert.False(capturedRequest.RequirePreferredExecutionProvider);
    }

    [Fact]
    public async Task EvaluateAsync_does_not_mark_dnnl_provider_selection_as_fallback()
    {
        StageRuntimePlanningRequest? capturedRequest = null;
        var planner = new FakeRuntimePlanner
        {
            PlanHandler = request =>
            {
                capturedRequest = request;
                return new StageRuntimePlan
                {
                    Stage = request.Stage,
                    Status = StageRuntimePlanStatus.Ready,
                    Variant = request.PreferredModelVariantAlias,
                    ExecutionProvider = ExecutionProviderKind.Dnnl
                };
            }
        };
        StarterPackCompatibilityService service = CreateService(
            runtimePlanner: planner,
            catalog: CreateCatalogWithUserPack("""
                {
                  "schema_version": 1,
                  "id": "dnnl-provider-pack",
                  "display_name": "DNNL Provider Pack",
                  "tier_preference": "balanced",
                  "description": "Tests DNNL provider planning.",
                  "profiles": [{ "id": "default", "display_name": "Default" }],
                  "models": [
                    {
                      "model_id": "onnx-community/silero-vad",
                      "stage": "vad",
                      "required": true,
                      "alias": "silero-vad",
                      "runtime_defaults": {
                        "cpu_safe": {
                          "variant": "default",
                          "execution_provider": "dnnl"
                        }
                      }
                    }
                  ]
                }
                """));

        StarterPackCompatibilityReport report = await service.EvaluateAsync(
            "dnnl-provider-pack",
            "default",
            StarterPackHardwareProfile.CpuSafe);

        StageCompatibilityEntry stage = Assert.Single(report.Stages);
        Assert.True(report.AllStagesRunnable);
        Assert.False(report.AnyFallbackApplied);
        Assert.False(stage.FallbackApplied);
        Assert.Equal("fully_compatible", report.CompatibilityStatus);
        Assert.Equal("onnxruntime-dnnl", stage.ResolvedExecutionProvider);
        Assert.NotNull(capturedRequest);
        Assert.Equal(ExecutionProviderKind.Dnnl, capturedRequest.PreferredExecutionProvider);
        Assert.True(capturedRequest.RequirePreferredExecutionProvider);
    }


    [Fact]
    public async Task EvaluateAsync_marks_model_not_runnable_when_vram_is_below_manifest_minimum()
    {
        StarterPackCatalog catalog = new();
        StarterPackDefinition basic = await catalog.GetAsync("basic");
        string modelId = StarterPackResolver.GetRequiredModelIds(basic, "default")[0];
        var hardwareProfiler = new FakeHardwareProfilerService
        {
            ViewState = new HardwareProfilerViewState(
                HardwareProfilerSnapshot.Create(
                    HardwareFingerprint.Create(
                        "Linux",
                        "x64",
                        "Tiny GPU",
                        totalRamBytes: 16L * 1024 * 1024 * 1024,
                        gpuDedicatedMemoryBytes: 1024L * 1024 * 1024),
                    [],
                    new HardwarePresetRecommendation(HardwareQualityPreset.Balanced, "Balanced", [], "balanced")),
                false,
                HardwareQualityPreset.Balanced,
                null,
                null,
                false,
                null)
        };
        BundledModelManifestRegistry registry = CreateRegistryWithEstimatedVram(
            modelId,
            estimatedVramMb: 4096,
            minVramMb: 2048,
            supportsPartialOffload: false);
        StarterPackCompatibilityService service = CreateService(
            runtimePlanner: CreateReadyPlanner(),
            hardwareProfiler: hardwareProfiler,
            manifestRegistry: registry);

        StarterPackCompatibilityReport report = await service.EvaluateAsync("basic", "default");

        Assert.False(report.AllStagesRunnable);
        Assert.Contains(report.Stages, stage =>
            !stage.Runnable &&
            stage.FallbackReason == "insufficient_vram");
    }

    private static StarterPackCompatibilityService CreateService(
        FakeRuntimePlanner? runtimePlanner = null,
        FakeHardwareProfilerService? hardwareProfiler = null,
        BundledModelManifestRegistry? manifestRegistry = null,
        StarterPackCatalog? catalog = null)
    {
        catalog ??= new StarterPackCatalog();
        hardwareProfiler ??= new FakeHardwareProfilerService();
        runtimePlanner ??= CreateDownloadRequiredPlanner();
        return new StarterPackCompatibilityService(catalog, hardwareProfiler, runtimePlanner, manifestRegistry);
    }

    private static BundledModelManifestRegistry CreateRegistryWithEstimatedVram(
        string modelId,
        int estimatedVramMb,
        int minVramMb,
        bool supportsPartialOffload)
    {
        string sourceManifestPath = Path.Combine(
            FindRepoRoot(),
            "src",
            "Trackdub.Inference",
            "Runtime",
            "ModelManifest",
            "bundled-models.manifest.json");
        string manifestRoot = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.StarterPackCompatibilityServiceTests",
            Guid.NewGuid().ToString("N"));
        string manifestPath = Path.Combine(manifestRoot, "bundled-models.manifest.json");
        Directory.CreateDirectory(manifestRoot);

        JsonObject root = JsonNode.Parse(File.ReadAllText(sourceManifestPath))!.AsObject();
        JsonObject model = root["models"]!.AsArray()
            .Select(node => node!.AsObject())
            .Single(candidate => candidate["model_id"]!.GetValue<string>().Equals(
                modelId,
                StringComparison.OrdinalIgnoreCase));
        model["estimated_vram_mb"] = estimatedVramMb;
        model["min_vram_mb"] = minVramMb;
        model["supports_partial_offload"] = supportsPartialOffload;

        File.WriteAllText(manifestPath, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        return BundledModelManifestRegistry.Load(manifestPath);
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
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("Could not locate repository root.");
    }

    private static StarterPackCatalog CreateCatalogWithUserPack(string json)
    {
        string root = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.StarterPackCompatibilityServiceTests",
            Guid.NewGuid().ToString("N"));
        string packsDirectory = Path.Combine(root, "StarterPacks");
        Directory.CreateDirectory(packsDirectory);
        File.WriteAllText(Path.Combine(packsDirectory, "auto-provider-pack.json"), json);
        return new StarterPackCatalog(new FakeAppStoragePaths(root));
    }

    private static FakeRuntimePlanner CreateDownloadRequiredPlanner() =>
        new()
        {
            PlanHandler = request => new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = StageRuntimePlanStatus.DownloadRequired,
                Variant = request.PreferredModelVariantAlias,
                ExecutionProvider = request.PreferredExecutionProvider,
                Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "cache miss")
            }
        };

    private static FakeRuntimePlanner CreateReadyPlanner() =>
        new()
        {
            PlanHandler = request => new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = StageRuntimePlanStatus.Ready,
                Variant = request.PreferredModelVariantAlias,
                ExecutionProvider = request.PreferredExecutionProvider ?? ExecutionProviderKind.Cpu
            }
        };

    private sealed class FakeAppStoragePaths(string root) : IAppStoragePaths
    {
        public string RootDirectory => root;
        public string UserDataRoot => root;
        public string UserCacheRoot => Path.Combine(root, "cache");
        public string? SharedAssetRoot => null;
        public bool IsPortable => true;
        public string ModelCacheDirectory => Path.Combine(root, "model-cache");
        public string ModelCacheIndexPath => Path.Combine(ModelCacheDirectory, "index.json");
        public string LogFilePath => Path.Combine(root, "trackdub.log");
        public string SettingsPath => Path.Combine(root, "settings.json");
        public string LayoutPath => Path.Combine(root, "layout.json");
        public string ToolCacheDirectory => Path.Combine(root, "tools");
        public string FfmpegToolCacheDirectory => Path.Combine(ToolCacheDirectory, "ffmpeg");
        public string EngineCacheDirectory => Path.Combine(root, "engines");
        public string ComponentCacheDirectory => Path.Combine(root, "components");
    }
}
