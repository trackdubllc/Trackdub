using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.FaceAnalysis;
using Trackdub.Inference.Onnx.LipSynthesis;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Media.Extraction;

namespace Trackdub.Composition.Tests.LipSynthesis;

/// <summary>
/// Shared helpers for M23 real-model integration tests. Never downloads; skips when cache or fixtures are absent.
/// </summary>
internal static class LipSynthesisIntegrationSupport
{
    internal const string LatentSyncModelId = "ByteDance/LatentSync-1.6";
    internal const string LatentSyncManifestAlias = "latentsync";
    internal const string LatentSyncEngineFamily = "latentsync-diffusion";
    internal const string ScfrdModelId = "InsightFace/scrfd-500m";
    internal const string LandmarkModelId = "InsightFace/2d106det";
    internal const string VideoFixtureEnvVar = "TRACKDUB_LIPSYNTH_VIDEO_FIXTURE";
    internal const string AudioFixtureEnvVar = "TRACKDUB_LIPSYNTH_AUDIO_FIXTURE";
    internal const string RequireSynthesizedEnvVar = "TRACKDUB_LIPSYNTH_REQUIRE_SYNTHESIZED";
    internal const string ExecutionProviderEnvVar = "TRACKDUB_LIPSYNTH_EP";

    internal static string ResolveCacheRoot()
    {
        string? configured = Environment.GetEnvironmentVariable("TRACKDUB_MODEL_CACHE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return Path.GetFullPath(configured);
        }

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Trackdub",
            "model-cache");
    }

    internal static string ResolveModelRoot(string modelId)
    {
        string root = ResolveCacheRoot();
        foreach (string part in modelId.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
            {
                throw new InvalidOperationException($"Model id '{modelId}' contains an unsafe path segment.");
            }

            root = Path.Combine(root, part);
        }

        return Path.GetFullPath(root);
    }

    internal static bool IsModelFilePresent(string modelId, string relativePath)
    {
        string fullPath = Path.Combine(
            ResolveModelRoot(modelId),
            relativePath.Replace('/', Path.DirectorySeparatorChar));
        return File.Exists(fullPath);
    }

    internal static bool AreLatentSyncModelsPresent() =>
        TryResolveLatentSyncModelRoot() is not null;

    internal static bool AreFaceModelsPresent() =>
        IsModelFilePresent(ScfrdModelId, "scrfd_500m.onnx") &&
        IsModelFilePresent(LandmarkModelId, "2d106det.onnx");

    internal static bool RequiresSynthesizedOutcome() =>
        string.Equals(Environment.GetEnvironmentVariable(RequireSynthesizedEnvVar), "1", StringComparison.Ordinal);

    internal static string? TryGetSkipReason()
    {
        string? latentSyncRoot = TryResolveLatentSyncModelRoot();
        if (latentSyncRoot is null)
        {
            return $"LatentSync ONNX bundle not present in model cache ({ResolveModelRoot(LatentSyncModelId)}). " +
                   "Download via Model Manager or `trackdub models download latentsync`.";
        }

        if (!AreFaceModelsPresent())
        {
            return $"SCRFD / 2d106 face models not present in model cache. " +
                   "Download companion models for lip synthesis (scrfd-500m, 2d106det).";
        }

        string? videoFixture = Environment.GetEnvironmentVariable(VideoFixtureEnvVar);
        if (string.IsNullOrWhiteSpace(videoFixture) || !File.Exists(videoFixture))
        {
            return $"{VideoFixtureEnvVar} is not set to an existing video file (MP4 with a visible frontal face).";
        }

        string? audioFixture = Environment.GetEnvironmentVariable(AudioFixtureEnvVar);
        if (string.IsNullOrWhiteSpace(audioFixture) || !File.Exists(audioFixture))
        {
            return $"{AudioFixtureEnvVar} is not set to an existing dubbed-audio WAV for the fixture video.";
        }

        return null;
    }

    /// <summary>
    /// When a GPU EP is explicitly requested via <see cref="ExecutionProviderEnvVar"/>, replace the ORT session
    /// factory's default (Shared) bootstrapper with the production-equivalent Windows bootstrapper wired with real
    /// TensorRT RTX plugin-directory providers. The default <c>TensorRtRtxPluginService.Shared</c> resolves null
    /// plugin directories, so it can never locate an installed plugin; the bare test host therefore always fell
    /// back to CPU. This mirrors how CompositionRoot / Benchmarks wire the bootstrapper so the smoke test can
    /// exercise the real Windows GPU path (TensorRT RTX, or DirectML via the WinML catalog).
    /// </summary>
    private static void ConfigureGpuBootstrapperIfRequested()
    {
        string? ep = Environment.GetEnvironmentVariable(ExecutionProviderEnvVar);
        if (string.IsNullOrWhiteSpace(ep) || string.Equals(ep, "CPU", StringComparison.OrdinalIgnoreCase))
        {
            return;
        }

#if WINDOWS
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        var storagePaths = new Trackdub.Infrastructure.Settings.TrackdubStoragePaths();
        var settingsService = new Trackdub.Infrastructure.Settings.JsonStudioSettingsService(storagePaths);

        var pluginService = new Trackdub.Inference.Onnx.TensorRtRtx.TensorRtRtxPluginService(
            explicitPluginDirectoryProvider: async cancellationToken =>
            {
                Trackdub.Contracts.StudioSettings settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
                return settings.TensorRtRtxPluginDirectory;
            },
            defaultInstallDirectoryProvider: _ => ValueTask.FromResult<string?>(
                Trackdub.Inference.Runtime.TensorRtRtx.TensorRtRtxProviderConstants.GetDefaultInstallDirectory(
                    storagePaths.UserDataRoot, "win-x64")),
            bundleEnsureAsync: null);

        var bootstrapper = new Trackdub.Inference.Onnx.ExecutionProviders.Windows.WindowsExecutionProviderBootstrapper(
            Trackdub.Inference.Onnx.WindowsMl.WindowsMlProviderRegistrationPolicy.Shared,
            DisabledNativeCudaTensorRtWindowsPolicy.Instance,
            pluginService);

        OnnxExecutionProviderBootstrapperRegistry.ResetForTests();
        OnnxExecutionProviderBootstrapperRegistry.Initialize(bootstrapper);
#endif
    }

#if WINDOWS
    /// <summary>Keeps native CUDA/TensorRT off so GPU requests route through the WinML catalog (TRT RTX / DirectML).</summary>
    private sealed class DisabledNativeCudaTensorRtWindowsPolicy
        : Trackdub.Contracts.ApplicationContracts.INativeCudaTensorRtWindowsPolicy
    {
        public static readonly DisabledNativeCudaTensorRtWindowsPolicy Instance = new();

        public Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(false);
    }
#endif

    internal static RealLipSynthesisStack CreateRealStack()
    {
        ConfigureGpuBootstrapperIfRequested();

        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out string? error)
            || registry is null)
        {
            throw new InvalidOperationException(error ?? "Failed to load bundled model manifest.");
        }

        BenchmarkModelPathResolver resolver = BenchmarkModelPathResolver.CreateDefault();
        var frameExtractor = new FfmpegVideoFrameExtractor();
        var audioExtractor = new FfmpegAudioSegmentExtractor();
        var planner = new CachedModelLipSynthesisRuntimePlanner(resolver);

        var engine = new LatentSyncOnnxLipSynthesisEngine(
            planner,
            resolver,
            frameExtractor,
            frameExtractor,
            audioExtractor,
            registry);

        var faceDetector = new ScfrdOnnxFaceDetector(resolver, frameExtractor);
        var landmarkProvider = new GeometryLandmarkProvider(faceDetector, frameExtractor, resolver);
        var poseEstimator = new PoseFromLandmarksEstimator(landmarkProvider);

        return new RealLipSynthesisStack(engine, faceDetector, landmarkProvider, poseEstimator);
    }

    private static string? TryResolveLatentSyncModelRoot()
    {
        BenchmarkModelPathResolver resolver = BenchmarkModelPathResolver.CreateDefault();
        BenchmarkModelResolutionResult discovery = resolver.Discover(LatentSyncManifestAlias);
        BenchmarkModelCandidate? candidate = discovery.Candidates.FirstOrDefault();
        if (candidate?.RootDirectory is { Length: > 0 } rootDirectory &&
            AreLatentSyncFilesPresent(rootDirectory))
        {
            return rootDirectory;
        }

        string fallbackRoot = ResolveModelRoot(LatentSyncModelId);
        return AreLatentSyncFilesPresent(fallbackRoot) ? fallbackRoot : null;
    }

    internal sealed record RealLipSynthesisStack(
        LatentSyncOnnxLipSynthesisEngine Engine,
        IFaceDetector FaceDetector,
        IFaceLandmarkProvider FaceLandmarkProvider,
        IFacePoseEstimator FacePoseEstimator);

    /// <summary>
    /// Returns a CPU-ready plan when LatentSync is cached; otherwise a blocked plan.
    /// </summary>
    private sealed class CachedModelLipSynthesisRuntimePlanner(BenchmarkModelPathResolver resolver) : IRuntimePlanner
    {
        private static ExecutionProviderKind ResolveExecutionProvider()
        {
            string? ep = Environment.GetEnvironmentVariable(ExecutionProviderEnvVar);
            return ep?.ToUpperInvariant() switch
            {
                "CUDA" => ExecutionProviderKind.Cuda,
                "DIRECTML" or "DML" => ExecutionProviderKind.DirectMl,
                "TENSORRTRTX" or "TRTRTX" => ExecutionProviderKind.TensorRTRtx,
                "TENSORRT" or "TRT" => ExecutionProviderKind.TensorRt,
                "QNN" or "QUALCOMM" => ExecutionProviderKind.Qnn,
                "MIGRAPHX" or "ROCM" => ExecutionProviderKind.Migraphx,
                "VITISAI" or "RYZENAI" or "NPU" => ExecutionProviderKind.VitisAi,
                "OPENVINO" => ExecutionProviderKind.OpenVinoCatalog,
                "COREML" => ExecutionProviderKind.CoreMl,
                _ => ExecutionProviderKind.Cpu,
            };
        }

        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();

            BenchmarkModelResolutionResult discovery = resolver.Discover(LatentSyncManifestAlias);
            BenchmarkModelCandidate? candidate = discovery.Candidates.FirstOrDefault();
            string? rootDirectory = candidate?.RootDirectory;
            if (candidate is null ||
                string.IsNullOrWhiteSpace(rootDirectory) ||
                !AreLatentSyncFilesPresent(rootDirectory))
            {
                return Task.FromResult(new StageRuntimePlan
                {
                    Stage = RuntimeStage.LipSynthesis,
                    Status = StageRuntimePlanStatus.Blocked,
                    ModelAlias = LatentSyncManifestAlias,
                    EngineFamily = LatentSyncEngineFamily,
                    Fallback = new RuntimePlanFallback(
                        RuntimePlanFallbackCode.ModelNotCached,
                        discovery.Error ?? "LatentSync is not cached."),
                });
            }

            ExecutionProviderKind ep = ResolveExecutionProvider();
            return Task.FromResult(new StageRuntimePlan
            {
                Stage = RuntimeStage.LipSynthesis,
                Status = StageRuntimePlanStatus.Ready,
                ModelId = LatentSyncModelId,
                ModelAlias = LatentSyncManifestAlias,
                EngineFamily = LatentSyncEngineFamily,
                ExecutionProvider = ep,
                ModelRootPath = rootDirectory,
                ModelEntryPath = Path.Combine(rootDirectory, "unet.onnx"),
            });
        }
    }

    private static bool AreLatentSyncFilesPresent(string modelRoot) =>
        Directory.Exists(modelRoot) &&
        File.Exists(Path.Combine(modelRoot, "unet.onnx")) &&
        File.Exists(Path.Combine(modelRoot, "vae_encoder.onnx")) &&
        File.Exists(Path.Combine(modelRoot, "vae_decoder.onnx")) &&
        File.Exists(Path.Combine(modelRoot, "whisper_encoder.onnx"));
}

/// <summary>
/// Skips unless LatentSync + face ONNX models are cached and video/audio fixtures are configured.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class LipSynthesisRealModelFactAttribute : FactAttribute
{
    public LipSynthesisRealModelFactAttribute(
        [System.Runtime.CompilerServices.CallerFilePath] string? sourceFilePath = null,
        [System.Runtime.CompilerServices.CallerLineNumber] int sourceLineNumber = -1)
        : base(sourceFilePath, sourceLineNumber)
    {
        Skip = LipSynthesisIntegrationSupport.TryGetSkipReason();
    }
}
