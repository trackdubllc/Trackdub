using System.Collections.Concurrent;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class RuntimePlannerBlackwellVariantTests
{
    private const string ValidSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public async Task PlanAsync_OnBlackwellWithMxfp8Cached_PrefersMxfp8ForTextRefinement()
    {
        using var workspace = new RuntimePlannerBlackwellTestWorkspace();
        BundledModelManifestRegistry registry = workspace.WriteTextRefinerManifest();

        string cacheRoot = workspace.CreateCacheRoot("tonythethompson/Qwen2.5-1.5B-Instruct");
        WriteBundledCacheFiles(workspace, registry, "text-refiner", cacheRoot, "mxfp8");

        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);

        RuntimePlanner planner = CreatePlanner(registry, cacheRoot, new FakeHardwareProfileProvider(hardware));

        StageRuntimePlan plan = await planner.PlanAsync(new StageRuntimePlanningRequest(RuntimeStage.TextRefinement));

        Assert.True(plan.IsRunnable(), $"Expected runnable plan but got {plan.Status}");
        Assert.Equal(ExecutionProviderKind.TensorRTRtx, plan.ExecutionProvider);
        Assert.Equal("mxfp8", plan.Variant);
    }

    [Fact]
    public void TryCreateDownloadRequiredPlan_OnBlackwellWithUnpinnedMxfp8_WhenDefaultCached_DoesNotRequestMxfp8()
    {
        using var workspace = new RuntimePlannerBlackwellTestWorkspace();
        BundledModelManifestRegistry registry = workspace.WriteTextRefinerManifestWithUnpinnedMxfp8();
        Assert.True(registry.TryResolve("text-refiner", out BundledModelManifestResolution? resolution));

        string cacheRoot = workspace.CreateCacheRoot("tonythethompson/Qwen2.5-1.5B-Instruct");
        WriteBundledCacheFiles(workspace, registry, "text-refiner", cacheRoot, "default");

        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);

        StageRuntimeRequirements requirements = StageRuntimeRequirementsCatalog.All[RuntimeStage.TextRefinement];
        var candidate = new RankedManifestEntry(resolution!.Entry, Rank: 0);
        var cacheIndex = new Dictionary<string, IReadOnlyList<LocalModelCacheRecord>>(StringComparer.Ordinal)
        {
            [resolution.Entry.ModelId] =
            [
                new LocalModelCacheRecord(
                    resolution.Entry.ModelId,
                    cacheRoot,
                    "main",
                    ValidSha256,
                    DateTimeOffset.UtcNow)
            ]
        };
        var fileExistenceCache = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var factory = new RuntimePlanFactory(new FakeExecutionProviderSmokeTester(_ => new ExecutionProviderSmokeTestResult(true)));

        StageRuntimePlan? plan = factory.TryCreateDownloadRequiredPlan(
            RuntimeStage.TextRefinement,
            requirements,
            candidate,
            hardware,
            [new(ExecutionProviderKind.TensorRTRtx, true)],
            cacheIndex,
            fileExistenceCache,
            preferredExecutionProvider: null,
            requirePreferredExecutionProvider: false,
            preferMigraphxOnAmdGpu: false,
            preferredModelVariantAlias: null);

        Assert.Null(plan);
    }

    [Fact]
    public async Task PlanAsync_OnBlackwellWithUnpinnedMxfp8_WhenUncached_RequestsDefaultNotMxfp8()
    {
        using var workspace = new RuntimePlannerBlackwellTestWorkspace();
        BundledModelManifestRegistry registry = workspace.WriteTextRefinerManifestWithUnpinnedMxfp8();

        string cacheRoot = workspace.CreateCacheRoot("tonythethompson/Qwen2.5-1.5B-Instruct");

        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);

        RuntimePlanner planner = CreatePlanner(
            registry,
            cacheRoot,
            new FakeHardwareProfileProvider(hardware),
            new FakeExecutionProviderSmokeTester(_ => new ExecutionProviderSmokeTestResult(true)));

        StageRuntimePlan plan = await planner.PlanAsync(new StageRuntimePlanningRequest(RuntimeStage.TextRefinement));

        Assert.Equal(StageRuntimePlanStatus.DownloadRequired, plan.Status);
        Assert.Equal("default", plan.Variant);
        Assert.NotEqual("mxfp8", plan.Variant);
    }

    [Fact]
    public async Task PlanAsync_AfterInvalidatePlanCache_ReplansForUpdatedGpuArchitecture()
    {
        using var workspace = new RuntimePlannerBlackwellTestWorkspace();
        BundledModelManifestRegistry registry = workspace.WriteTextRefinerManifest();

        string cacheRoot = workspace.CreateCacheRoot("tonythethompson/Qwen2.5-1.5B-Instruct");
        WriteBundledCacheFiles(workspace, registry, "text-refiner", cacheRoot, "mxfp8");
        WriteBundledCacheFiles(workspace, registry, "text-refiner", cacheRoot, "default");

        var blackwellHardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);
        var adaHardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 4090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Ada);

        var hardwareProvider = new MutableHardwareProfileProvider(blackwellHardware);
        RuntimePlanner planner = CreatePlanner(registry, cacheRoot, hardwareProvider);

        StageRuntimePlan blackwellPlan = await planner.PlanAsync(new StageRuntimePlanningRequest(RuntimeStage.TextRefinement));
        Assert.Equal("mxfp8", blackwellPlan.Variant);

        hardwareProvider.Set(adaHardware);
        planner.InvalidatePlanCache();

        StageRuntimePlan adaPlan = await planner.PlanAsync(new StageRuntimePlanningRequest(RuntimeStage.TextRefinement));
        Assert.True(adaPlan.IsRunnable(), $"Expected runnable plan but got {adaPlan.Status}");
        Assert.NotEqual("mxfp8", adaPlan.Variant);
    }

    [Fact]
    public void TryCreateDownloadRequiredPlan_WithDefaultCachedAndUnpinnedMxfp8_DoesNotPlanMxfp8Download()
    {
        using var workspace = new RuntimePlannerBlackwellTestWorkspace();
        BundledModelManifestRegistry registry = workspace.WriteTextRefinerManifest(includeMxfp8Hashes: false);
        Assert.True(registry.TryResolve("text-refiner", out BundledModelManifestResolution? resolution));
        BundledModelManifestEntry entry = resolution!.Entry;

        string cacheRoot = workspace.CreateCacheRoot("tonythethompson/Qwen2.5-1.5B-Instruct");
        WriteBundledCacheFiles(workspace, registry, "text-refiner", cacheRoot, "default");

        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5090",
            NvidiaGpuArchitecture: NvidiaGpuArchitectureBucket.Blackwell);
        var requirements = new StageRuntimeRequirements(
            RuntimeStage.TextRefinement,
            ModelTask.TextRefinement,
            ["text-refiner"],
            [ExecutionProviderKind.TensorRTRtx],
            ["default"],
            ["default"]);
        var cacheIndex = new Dictionary<string, IReadOnlyList<LocalModelCacheRecord>>(StringComparer.OrdinalIgnoreCase)
        {
            [entry.ModelId] = [new(entry.ModelId, cacheRoot, "main", ValidSha256, DateTimeOffset.UtcNow)]
        };
        var factory = new RuntimePlanFactory(new FakeExecutionProviderSmokeTester(_ => new ExecutionProviderSmokeTestResult(false)));

        StageRuntimePlan? plan = factory.TryCreateDownloadRequiredPlan(
            RuntimeStage.TextRefinement,
            requirements,
            new RankedManifestEntry(entry, 0),
            hardware,
            [new(ExecutionProviderKind.TensorRTRtx, true)],
            cacheIndex,
            new System.Collections.Concurrent.ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase),
            preferredExecutionProvider: null,
            requirePreferredExecutionProvider: false,
            preferMigraphxOnAmdGpu: false,
            preferredModelVariantAlias: null);

        Assert.Null(plan);
    }

    private static RuntimePlanner CreatePlanner(
        BundledModelManifestRegistry registry,
        string cacheRoot,
        IHardwareProfileProvider hardwareProvider,
        IExecutionProviderSmokeTester? smokeTester = null) =>
        new(
            registry,
            hardwareProvider,
            new FakeExecutionProviderDiscovery(
            [
                new(ExecutionProviderKind.TensorRTRtx, true),
                new(ExecutionProviderKind.DirectMl, true)
            ]),
            smokeTester ?? new FakeExecutionProviderSmokeTester(_ => new ExecutionProviderSmokeTestResult(true)),
            new InMemoryModelCacheInventory(
            [
                new("tonythethompson/Qwen2.5-1.5B-Instruct", cacheRoot, "main", ValidSha256, DateTimeOffset.UtcNow)
            ]));

    private static void WriteBundledCacheFiles(
        RuntimePlannerBlackwellTestWorkspace workspace,
        BundledModelManifestRegistry registry,
        string alias,
        string cacheRoot,
        string variantAlias)
    {
        Assert.True(registry.TryResolve(alias, out BundledModelManifestResolution? resolution));
        BundledModelManifestEntry entry = resolution!.Entry;

        foreach (string relativePath in entry.DownloadFiles)
        {
            workspace.WriteCacheFile(cacheRoot, relativePath);
        }

        BundledModelManifestVariant? variant = entry.Variants
            .FirstOrDefault(candidate => candidate.Alias.Equals(variantAlias, StringComparison.OrdinalIgnoreCase));
        string entryPath = variant?.EntryPath ?? entry.DefaultBenchmarkEntryPath;
        workspace.WriteCacheFile(cacheRoot, Path.GetRelativePath(entry.RootDirectory, entryPath));

        if (variant is not null)
        {
            foreach (string relativePath in variant.DownloadFiles)
            {
                workspace.WriteCacheFile(cacheRoot, relativePath);
            }
        }
    }

    private sealed class MutableHardwareProfileProvider : IHardwareProfileProvider
    {
        private HardwareProfile _profile;

        public MutableHardwareProfileProvider(HardwareProfile profile) => _profile = profile;

        public void Set(HardwareProfile profile) => _profile = profile;

        public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(_profile);
    }

    private sealed class FakeHardwareProfileProvider(HardwareProfile profile) : IHardwareProfileProvider
    {
        public Task<HardwareProfile> GetCurrentAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(profile);
    }

    private sealed class FakeExecutionProviderDiscovery(IReadOnlyList<ExecutionProviderAvailability> availabilities)
        : IExecutionProviderDiscovery
    {
        public Task<IReadOnlyList<ExecutionProviderAvailability>> DiscoverAsync(
            HardwareProfile hardwareProfile,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(availabilities);
    }

    private sealed class FakeExecutionProviderSmokeTester(
        Func<ExecutionProviderSmokeTestRequest, ExecutionProviderSmokeTestResult> handler)
        : IExecutionProviderSmokeTester
    {
        public Task<ExecutionProviderSmokeTestResult> SmokeTestAsync(
            ExecutionProviderSmokeTestRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(request));
    }

    private sealed class InMemoryModelCacheInventory(IReadOnlyList<LocalModelCacheRecord> records) : IModelCacheInventory
    {
        public Task<IReadOnlyList<LocalModelCacheRecord>> LoadAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(records);
    }

    private sealed class RuntimePlannerBlackwellTestWorkspace : IDisposable
    {
        public RuntimePlannerBlackwellTestWorkspace()
        {
            RootPath = Path.Combine(Path.GetTempPath(), $"trackdub-blackwell-planner-{Guid.NewGuid():N}");
            Directory.CreateDirectory(RootPath);
        }

        public string RootPath { get; }

        public BundledModelManifestRegistry WriteTextRefinerManifest(bool includeMxfp8Hashes = true)
        {
            string manifestPath = Path.Combine(RootPath, "bundled-models.manifest.json");
            string json = """
                {
                  "models": [
                    {
                      "model_id": "tonythethompson/Qwen2.5-1.5B-Instruct",
                      "task": "text-refinement",
                      "engine_family": "qwen-instruct",
                      "capabilities": ["transcript-polishing"],
                      "tier": "balanced",
                      "license": "Apache-2.0",
                      "commercial_allowed": true,
                      "redistribution_allowed": true,
                      "requires_attribution": false,
                      "requires_user_consent": false,
                      "voice_cloning": false,
                      "commercial_use_verified": true,
                      "source_url": "https://huggingface.co/tonythethompson/Qwen2.5-1.5B-Instruct",
                      "revision": "main",
                      "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "aliases": ["qwen2.5-1.5b-instruct", "text-refiner"],
                      "root_path": "./manifest-models/qwen-polisher",
                      "benchmark_entry": "genai_config.json",
                      "download_files": ["genai_config.json", "model.onnx"],
                      "download_file_hashes": {
                        "genai_config.json": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        "model.onnx": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                      },
                      "variants": [
                        {
                          "alias": "default",
                          "entry_path": "genai_config.json",
                          "is_default": true,
                          "download_files": ["genai_config.json", "model.onnx"]
                        },
                        {
                          "alias": "mxfp8",
                          "entry_path": "mxfp8/genai_config.json",
                          "supported_providers": ["trt-rtx"],
                          "download_files": ["mxfp8/genai_config.json", "mxfp8/model.onnx", "mxfp8/model.onnx.data"]
                        }
                      ]
                    }
                  ]
                }
                """;

            if (includeMxfp8Hashes)
            {
                json = json.Replace(
                    "\"model.onnx\": \"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\"",
                    "\"model.onnx\": \"cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc\",\n" +
                    "                        \"mxfp8/genai_config.json\": \"dddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddddd\",\n" +
                    "                        \"mxfp8/model.onnx\": \"eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee\",\n" +
                    "                        \"mxfp8/model.onnx.data\": \"ffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffffff\"",
                    StringComparison.Ordinal);
            }

            File.WriteAllText(manifestPath, json);
            return BundledModelManifestRegistry.Load(manifestPath);
        }

        public BundledModelManifestRegistry WriteTextRefinerManifestWithUnpinnedMxfp8()
        {
            string manifestPath = Path.Combine(RootPath, "bundled-models-unpinned-mxfp8.manifest.json");
            const string json = """
                {
                  "models": [
                    {
                      "model_id": "tonythethompson/Qwen2.5-1.5B-Instruct",
                      "task": "text-refinement",
                      "engine_family": "qwen-instruct",
                      "capabilities": ["transcript-polishing"],
                      "tier": "balanced",
                      "license": "Apache-2.0",
                      "commercial_allowed": true,
                      "redistribution_allowed": true,
                      "requires_attribution": false,
                      "requires_user_consent": false,
                      "voice_cloning": false,
                      "commercial_use_verified": true,
                      "source_url": "https://huggingface.co/tonythethompson/Qwen2.5-1.5B-Instruct",
                      "revision": "main",
                      "sha256": "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa",
                      "aliases": ["qwen2.5-1.5b-instruct", "text-refiner"],
                      "root_path": "./manifest-models/qwen-polisher",
                      "benchmark_entry": "genai_config.json",
                      "download_files": ["genai_config.json", "model.onnx"],
                      "download_file_hashes": {
                        "genai_config.json": "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb",
                        "model.onnx": "cccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccccc"
                      },
                      "variants": [
                        {
                          "alias": "default",
                          "entry_path": "genai_config.json",
                          "is_default": true,
                          "download_files": ["genai_config.json", "model.onnx"]
                        },
                        {
                          "alias": "mxfp8",
                          "entry_path": "mxfp8/genai_config.json",
                          "supported_providers": ["trt-rtx"],
                          "download_files": ["mxfp8/genai_config.json", "mxfp8/model.onnx", "mxfp8/model.onnx.data"]
                        }
                      ]
                    }
                  ]
                }
                """;

            File.WriteAllText(manifestPath, json);
            return BundledModelManifestRegistry.Load(manifestPath);
        }

        public string CreateCacheRoot(string name)
        {
            string cacheRoot = Path.Combine(RootPath, "machine-cache", name);
            Directory.CreateDirectory(cacheRoot);
            return cacheRoot;
        }

        public void WriteCacheFile(string cacheRoot, string relativePath)
        {
            string filePath = Path.Combine(cacheRoot, relativePath);
            string? directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrWhiteSpace(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllText(filePath, "placeholder");
        }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(RootPath))
                {
                    Directory.Delete(RootPath, recursive: true);
                }
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }
}
