using Microsoft.Extensions.Logging.Abstractions;
using Trackdub.Contracts;
using Trackdub.Composition.Runtime;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;
using Xunit;

namespace Trackdub.Composition.Tests.Runtime;

public sealed class RuntimeSelectionServiceTests
{
    private readonly FakeExecutionProviderDiscovery discovery = new();
    private readonly FakeHardwareProfileProvider hardware = new();
    private readonly FakeRuntimePlanner planner = new();
    private readonly FakeHardwareProfilerService profiler = new();
    private readonly RuntimeSelectionService service;

    public RuntimeSelectionServiceTests()
    {
        service = new RuntimeSelectionService(discovery, hardware, planner, profiler, NullLogger<RuntimeSelectionService>.Instance);
    }

    [Fact]
    public async Task SelectRouteAsync_NoPreference_UsesPlannerSelectedProvider()
    {
        planner.PlanHandler = request => CreatePlan(request.Stage, ExecutionProviderKind.TensorRTRtx);

        var route = await service.SelectRouteAsync(RuntimeStage.Asr);

        Assert.Equal(ExecutionProviderKind.TensorRTRtx, route.SelectedProvider);
        Assert.Equal(RuntimeRouteReadiness.Ready, route.Readiness);
    }

    [Fact]
    public async Task SelectRouteAsync_WhenPlannerSelectsTensorRt_UsesTensorRt()
    {
        hardware.Profile = new HardwareProfile("linux", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 4090");
        planner.PlanHandler = request => CreatePlan(request.Stage, ExecutionProviderKind.TensorRt);

        var route = await service.SelectRouteAsync(RuntimeStage.Asr);

        Assert.Equal(ExecutionProviderKind.TensorRt, route.SelectedProvider);
    }

    [Fact]
    public async Task SelectRouteAsync_WhenPlannerSelectsCuda_UsesCuda()
    {
        hardware.Profile = new HardwareProfile("linux", "x64", HasGpu: true, GpuDescription: "NVIDIA RTX 4090");
        planner.PlanHandler = request => CreatePlan(request.Stage, ExecutionProviderKind.Cuda);

        var route = await service.SelectRouteAsync(RuntimeStage.Asr);

        Assert.Equal(ExecutionProviderKind.Cuda, route.SelectedProvider);
    }

    [Fact]
    public async Task SelectRouteAsync_PreferenceAvailable_HonorsPreference()
    {
        StageRuntimePlanningRequest? capturedRequest = null;
        planner.PlanHandler = request =>
        {
            capturedRequest = request;
            return CreatePlan(request.Stage, ExecutionProviderKind.DirectMl);
        };

        var route = await service.SelectRouteAsync(RuntimeStage.Asr, ExecutionProviderKind.DirectMl);

        Assert.Equal(ExecutionProviderKind.DirectMl, route.SelectedProvider);
        Assert.Equal(ExecutionProviderKind.DirectMl, capturedRequest?.PreferredExecutionProvider);
    }

    [Fact]
    public async Task SelectRouteAsync_PreferenceUnavailable_FallsBackToCpu()
    {
        planner.PlanHandler = request => CreatePlan(request.Stage, ExecutionProviderKind.Cpu) with
        {
            Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ProviderUnavailable, "No DirectML")
        };

        var route = await service.SelectRouteAsync(RuntimeStage.Asr, ExecutionProviderKind.DirectMl);

        Assert.Equal(ExecutionProviderKind.Cpu, route.SelectedProvider);
        Assert.Equal(RuntimeRouteReadiness.Fallback, route.Readiness);
        Assert.Contains("No DirectML", route.FallbackReason);
    }

    [Fact]
    public async Task SelectRouteAsync_NoPreference_UsesPlannerStageConstraints()
    {
        discovery.Availabilities =
        [
            new(ExecutionProviderKind.Cpu, true),
            new(ExecutionProviderKind.DirectMl, true),
            new(ExecutionProviderKind.TensorRTRtx, true)
        ];
        planner.PlanHandler = request =>
        {
            Assert.Equal(RuntimeStage.Separation, request.Stage);
            return CreatePlan(request.Stage, ExecutionProviderKind.DirectMl) with
            {
                ModelId = "spleeter-2stems",
                Variant = "default"
            };
        };

        RuntimeRoute route = await service.SelectRouteAsync(RuntimeStage.Separation);

        Assert.Equal(ExecutionProviderKind.DirectMl, route.SelectedProvider);
        Assert.Equal("spleeter-2stems", route.ModelId);
        Assert.Equal("default", route.Variant);
    }

    [Fact]
    public async Task SelectRouteAsync_DownloadRequired_IsNotReady()
    {
        planner.PlanHandler = request => CreatePlan(request.Stage, ExecutionProviderKind.TensorRTRtx) with
        {
            Status = StageRuntimePlanStatus.DownloadRequired,
            Fallback = new RuntimePlanFallback(RuntimePlanFallbackCode.ModelNotCached, "model file missing")
        };

        RuntimeRoute route = await service.SelectRouteAsync(RuntimeStage.Asr);

        Assert.Equal(RuntimeRouteReadiness.NotReady, route.Readiness);
        Assert.Contains("model file missing", route.FallbackReason);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_MapsAllProviders()
    {
        discovery.Availabilities =
        [
            new(ExecutionProviderKind.Cpu, true),
            new(ExecutionProviderKind.DirectMl, true),
            new(ExecutionProviderKind.TensorRTRtx, false, "Not an NVIDIA GPU")
        ];

        var capabilities = await service.GetCapabilitiesAsync();

        Assert.Equal(3, capabilities.Count);
        Assert.True(capabilities.Single(c => c.Provider == ExecutionProviderKind.Cpu).ProviderLoadable);
        Assert.True(capabilities.Single(c => c.Provider == ExecutionProviderKind.DirectMl).ProviderLoadable);
        Assert.False(capabilities.Single(c => c.Provider == ExecutionProviderKind.TensorRTRtx).ProviderLoadable);
        Assert.Equal("Not an NVIDIA GPU", capabilities.Single(c => c.Provider == ExecutionProviderKind.TensorRTRtx).BlockedReason);
    }

    [Fact]
    public async Task GetCapabilitiesAsync_DoesNotClaimRuntimeOrSmokeEvidenceWithoutChecks()
    {
        discovery.Availabilities =
        [
            new(ExecutionProviderKind.Cpu, true),
            new(ExecutionProviderKind.DirectMl, true)
        ];

        IReadOnlyList<ProviderCapability> capabilities = await service.GetCapabilitiesAsync();

        Assert.All(capabilities, capability =>
        {
            Assert.False(capability.RuntimePackageInstalled);
            Assert.False(capability.ModelVariantCompatible);
            Assert.False(capability.SmokeTestPassed);
        });
    }

    [Fact]
    public async Task SelectRouteAsync_WhenProfilerHasEvidence_ExposesBenchmarkEvidenceId()
    {
        planner.PlanHandler = request => CreatePlan(request.Stage, ExecutionProviderKind.TensorRTRtx);
        Guid evidenceId = Guid.NewGuid();
        profiler.ViewState = new HardwareProfilerViewState(
            new HardwareProfilerSnapshot(
                evidenceId,
                HardwareFingerprint.Create("windows", "x64", "GPU", 8L * 1024 * 1024 * 1024, 4L * 1024 * 1024 * 1024),
                [],
                new HardwarePresetRecommendation(
                    HardwareQualityPreset.Balanced,
                    "Balanced",
                    ["test"],
                    "balanced"),
                DateTimeOffset.UtcNow),
            IsStale: false,
            EffectivePreset: HardwareQualityPreset.Balanced,
            EffectiveRecommendation: null,
            OverridePresetKey: null,
            HasOverride: false,
            EvidenceIdForPlanner: evidenceId.ToString());

        RuntimeRoute route = await service.SelectRouteAsync(RuntimeStage.Asr);

        Assert.Equal(evidenceId.ToString(), route.BenchmarkEvidenceId);
    }

    private static StageRuntimePlan CreatePlan(
        RuntimeStage stage,
        ExecutionProviderKind provider,
        StageRuntimePlanStatus status = StageRuntimePlanStatus.Ready) =>
        new()
        {
            Stage = stage,
            Status = status,
            ExecutionProvider = provider
        };
}
