using Trackdub.Composition.StarterPacks;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Tests;

public sealed class StarterPackGpuSetupAdvisorTests
{
    [Fact]
    public void ResolveRecommendedRuntimeKind_prefers_tensorrt_for_nvidia_hardware()
    {
        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "NVIDIA GeForce RTX 5070");

        var stages = new[]
        {
            CreateStage("vad", "directml", "cpu"),
        };

        StarterPackGpuRuntimeKind kind =
            StarterPackGpuSetupAdvisor.ResolveRecommendedRuntimeKind(hardware, stages);

        Assert.Equal(StarterPackGpuRuntimeKind.NvidiaTensorRtRtx, kind);
    }

    [Fact]
    public void ResolveRecommendedRuntimeKind_prefers_migraphx_for_amd_hardware()
    {
        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "AMD Radeon RX 7900 XTX");

        var stages = new[]
        {
            CreateStage("vad", "directml", "cpu"),
        };

        StarterPackGpuRuntimeKind kind =
            StarterPackGpuSetupAdvisor.ResolveRecommendedRuntimeKind(hardware, stages);

        Assert.Equal(StarterPackGpuRuntimeKind.AmdMigraphx, kind);
    }

    [Fact]
    public void ResolveRecommendedRuntimeKind_prefers_openvino_for_intel_hardware()
    {
        var hardware = new HardwareProfile(
            "windows",
            "x64",
            HasGpu: true,
            GpuDescription: "Intel(R) Arc(TM) A770 Graphics");

        StarterPackGpuRuntimeKind kind = StarterPackGpuSetupAdvisor.ResolveRecommendedRuntimeKind(
            hardware,
            [CreateStage("vad", "directml", "cpu")]);

        Assert.Equal(StarterPackGpuRuntimeKind.IntelOpenVino, kind);
    }

    [Fact]
    public void ResolveRecommendedRuntimeKind_prefers_qnn_for_qualcomm_hardware()
    {
        var hardware = new HardwareProfile(
            "windows",
            "arm64",
            HasGpu: true,
            GpuDescription: "Qualcomm Adreno GPU");

        StarterPackGpuRuntimeKind kind = StarterPackGpuSetupAdvisor.ResolveRecommendedRuntimeKind(
            hardware,
            [CreateStage("vad", "directml", "cpu")]);

        Assert.Equal(StarterPackGpuRuntimeKind.QualcommQnn, kind);
    }

    [Fact]
    public void BuildCompatibilityWarnings_suppresses_per_stage_lines_when_gpu_setup_present()
    {
        var gpuSetup = new StarterPackGpuSetupHint(
            StarterPackGpuRuntimeKind.NvidiaTensorRtRtx,
            CanInstall: true,
            RequiresGpuVariantOptimization: false,
            GpuFallbackStageCount: 3);

        IReadOnlyList<string>? warnings = StarterPackPresentationService.BuildCompatibilityWarnings(
            new StarterPackCompatibilityReport(
                "premium",
                "default",
                "balanced_gpu",
                [CreateStage("vad", "directml", "cpu")],
                AllStagesRunnable: true,
                AnyFallbackApplied: true),
            gpuSetup);

        Assert.Null(warnings);
    }

    private static StageCompatibilityEntry CreateStage(
        string alias,
        string requestedEp,
        string resolvedEp) =>
        new(
            StageNames.Vad,
            alias,
            "default",
            requestedEp,
            "default",
            resolvedEp,
            FallbackApplied: true,
            FallbackReason: "ep_unavailable",
            Runnable: true);
}
