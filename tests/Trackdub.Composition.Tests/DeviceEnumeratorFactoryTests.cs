using Microsoft.Extensions.DependencyInjection;
using Trackdub.Composition.Headless;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition.Tests;

/// <summary>
/// Callers that must describe the host's adapters before a container exists (the benchmark CLI's
/// capacity banner) use <see cref="DeviceEnumeratorFactory"/> instead of resolving the registered
/// enumerator. The two must report the same adapters, or a bound's feasibility would be judged
/// against hardware the run will not use.
/// </summary>
public sealed class DeviceEnumeratorFactoryTests
{
    [Fact]
    public async Task Create_reports_the_gpu_adapters_the_registered_enumerator_reports()
    {
        var services = new ServiceCollection();
        services.AddHeadlessTrackdub();
        using ServiceProvider provider = services.BuildServiceProvider();
        IReadOnlyList<DeviceEntry> registered = await provider
            .GetRequiredService<IDeviceEnumerator>()
            .GetDevicesAsync(TestContext.Current.CancellationToken);

        IReadOnlyList<DeviceEntry> direct = await DeviceEnumeratorFactory
            .Create()
            .GetDevicesAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(direct);
        Assert.Equal(
            registered.Where(IsGpuAdapter).Select(GpuAdapterProjection),
            direct.Where(IsGpuAdapter).Select(GpuAdapterProjection));
    }

    [Fact]
    public async Task Create_reports_a_cpu_fallback_so_callers_always_get_a_device_list()
    {
        IReadOnlyList<DeviceEntry> devices = await DeviceEnumeratorFactory
            .Create()
            .GetDevicesAsync(TestContext.Current.CancellationToken);

        Assert.Contains(devices, device => device.Kind is DeviceKind.Cpu);
    }

    private static bool IsGpuAdapter(DeviceEntry device) =>
        device.Kind is DeviceKind.DiscreteGpu or DeviceKind.IntegratedGpu;

    // Supported providers are excluded deliberately: the factory has no OpenVINO runtime to
    // probe, so only the memory-bearing GPU adapter rows must match.
    private static object GpuAdapterProjection(DeviceEntry device) => new
    {
        device.Kind,
        device.DeviceIndex,
        device.AdapterDescription,
        device.VendorName,
        device.DedicatedVramMb,
        device.SharedMemoryMb,
    };
}
