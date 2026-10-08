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
        Assert.Equal(4096, Resolve(1, ExecutionProviderKind.DirectMl, LargeGpu, SmallGpu));
        Assert.Equal(24576, Resolve(0, ExecutionProviderKind.DirectMl, LargeGpu, SmallGpu));
    }

    [Fact]
    public void NoPlannedDevice_DirectMl_UsesDeviceZero()
    {
        // DirectML binds device id 0, the first hardware adapter.
        Assert.Equal(24576, Resolve(null, ExecutionProviderKind.DirectMl, LargeGpu, SmallGpu));
    }

    [Fact]
    public void NoPlannedDevice_HybridLaptop_NvidiaProviderUsesNvidiaGpu()
    {
        // iGPU enumerates first, but CUDA/TensorRT RTX device 0 is the first NVIDIA GPU.
        DeviceEntry iGpu = Gpu(index: 0, DeviceKind.IntegratedGpu, vramMb: 128, vendor: "Intel", sharedMb: 16384);
        DeviceEntry nvidia = Gpu(index: 1, DeviceKind.DiscreteGpu, vramMb: 8192, vendor: "NVIDIA");

        Assert.Equal(8192, Resolve(null, ExecutionProviderKind.TensorRTRtx, iGpu, nvidia));
        Assert.Equal(8192, Resolve(null, ExecutionProviderKind.Cuda, iGpu, nvidia));
        // DirectML device 0 is the iGPU here; its allocations come from shared memory.
        Assert.Equal(128 + 16384, Resolve(null, ExecutionProviderKind.DirectMl, iGpu, nvidia));
    }

    [Fact]
    public void UnidentifiedDevice_AssumesSmallestGpuMemory()
    {
        Assert.Equal(4096, Resolve(7, ExecutionProviderKind.DirectMl, LargeGpu, SmallGpu));
        Assert.Equal(4096, Resolve(null, ExecutionProviderKind.Migraphx, LargeGpu, SmallGpu));
    }

    [Fact]
    public void DeviceWithoutMemoryReading_AssumesSmallestGpuMemory()
    {
        Assert.Equal(4096, Resolve(2, ExecutionProviderKind.DirectMl, LargeGpu, SmallGpu, IntegratedGpu));
        Assert.Null(Resolve(null, ExecutionProviderKind.DirectMl));
    }

    private static long? Resolve(int? deviceIndex, ExecutionProviderKind provider, params DeviceEntry[] devices) =>
        RoutedTtsEngine.ResolvePlannedDeviceVramMb(deviceIndex, provider, devices);

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

    private static DeviceEntry Gpu(int index, DeviceKind kind, int vramMb, string vendor = "Test", int sharedMb = 0) =>
        new(kind, index, $"GPU {index}", vendor, vramMb, sharedMb, SupportedProviders: [ExecutionProviderKind.DirectMl]);

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
