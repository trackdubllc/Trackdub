using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Tests;

/// <summary>
/// TTS placement for the concurrency cap: the bound must use the memory of the device the plan
/// selects, not the largest adapter on the host.
/// </summary>
public sealed class RoutedTtsEnginePlacementTests
{
    private static readonly DeviceEntry LargeGpu = Gpu(index: 0, DeviceKind.DiscreteGpu, vramMb: 24576);
    private static readonly DeviceEntry SmallGpu = Gpu(index: 1, DeviceKind.DiscreteGpu, vramMb: 4096);
    private static readonly DeviceEntry IntegratedGpu = Gpu(index: 2, DeviceKind.IntegratedGpu, vramMb: 0);

    [Fact]
    public void PlannedDevice_UsesThatDevicesVram()
    {
        Assert.Equal(4096, RoutedTtsEngine.ResolvePlannedDeviceVramMb(1, [LargeGpu, SmallGpu]));
        Assert.Equal(24576, RoutedTtsEngine.ResolvePlannedDeviceVramMb(0, [LargeGpu, SmallGpu]));
    }

    [Fact]
    public void UnknownDevice_AssumesSmallestGpuWithDedicatedMemory()
    {
        Assert.Equal(4096, RoutedTtsEngine.ResolvePlannedDeviceVramMb(null, [LargeGpu, SmallGpu, IntegratedGpu]));
        Assert.Equal(4096, RoutedTtsEngine.ResolvePlannedDeviceVramMb(7, [LargeGpu, SmallGpu]));
    }

    [Fact]
    public void PlannedDeviceWithoutDedicatedMemory_IsUnknown()
    {
        Assert.Null(RoutedTtsEngine.ResolvePlannedDeviceVramMb(2, [LargeGpu, IntegratedGpu]));
        Assert.Null(RoutedTtsEngine.ResolvePlannedDeviceVramMb(null, []));
    }

    [Fact]
    public async Task ResolvePlacementAsync_GpuPlan_ReportsPlannedDeviceVram()
    {
        var planner = new FixedPlanner(ExecutionProviderKind.DirectMl, deviceIndex: 1);
        var engine = new RoutedTtsEngine(planner, [], deviceEnumerator: new FixedDevices([LargeGpu, SmallGpu]));

        TtsAcceleratorPlacement? placement = await engine.ResolvePlacementAsync(
            new InferenceRequestOptions(PreferredModelAlias: "cosyvoice-300m", RequirePreferredModelAlias: true),
            "en",
            TestContext.Current.CancellationToken);

        Assert.Equal(new TtsAcceleratorPlacement(AcceleratorRouted: true, DeviceVramMb: 4096), placement);
        Assert.Equal("cosyvoice-300m", planner.LastRequest?.NormalizedPreferredModelAlias);
        Assert.True(planner.LastRequest?.RequirePreferredModelAlias);
        Assert.Equal("en", planner.LastRequest?.SourceLanguage);
    }

    [Theory]
    [InlineData(ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.Dnnl)]
    public async Task ResolvePlacementAsync_CpuPlan_IsNotAcceleratorRouted(ExecutionProviderKind provider)
    {
        var engine = new RoutedTtsEngine(new FixedPlanner(provider, deviceIndex: null), [], deviceEnumerator: new FixedDevices([SmallGpu]));

        TtsAcceleratorPlacement? placement = await engine.ResolvePlacementAsync(
            InferenceRequestOptions.Default, "en", TestContext.Current.CancellationToken);

        Assert.Equal(new TtsAcceleratorPlacement(AcceleratorRouted: false, DeviceVramMb: null), placement);
    }

    [Fact]
    public async Task ResolvePlacementAsync_NoRunnablePlan_ReturnsNull()
    {
        var engine = new RoutedTtsEngine(new FixedPlanner(provider: null, deviceIndex: null), []);

        Assert.Null(await engine.ResolvePlacementAsync(
            InferenceRequestOptions.Default, "en", TestContext.Current.CancellationToken));
    }

    private static DeviceEntry Gpu(int index, DeviceKind kind, int vramMb) =>
        new(kind, index, $"GPU {index}", "Test", vramMb, SharedMemoryMb: 0, SupportedProviders: [ExecutionProviderKind.DirectMl]);

    private sealed class FixedPlanner(ExecutionProviderKind? provider, int? deviceIndex) : IRuntimePlanner
    {
        public StageRuntimePlanningRequest? LastRequest { get; private set; }

        public Task<StageRuntimePlan> PlanAsync(
            StageRuntimePlanningRequest request,
            CancellationToken cancellationToken = default)
        {
            LastRequest = request;
            return Task.FromResult(new StageRuntimePlan
            {
                Stage = request.Stage,
                Status = provider is null ? StageRuntimePlanStatus.Blocked : StageRuntimePlanStatus.Ready,
                ExecutionProvider = provider,
                DeviceIndex = deviceIndex
            });
        }
    }

    private sealed class FixedDevices(IReadOnlyList<DeviceEntry> devices) : IDeviceEnumerator
    {
        public Task<IReadOnlyList<DeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(devices);

        public Task<IReadOnlyList<DeviceEntry>> ReEnumerateAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult(devices);
    }
}
