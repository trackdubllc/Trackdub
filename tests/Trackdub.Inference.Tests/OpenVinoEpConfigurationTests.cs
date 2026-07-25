using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Unit tests for OpenVINO EP configuration behavior:
/// device type string selection, CPU proxy mode, and fallback when not installed.
/// </summary>
public sealed class OpenVinoEpConfigurationTests : IDisposable
{
    private readonly List<string> _tempDirs = [];

    public void Dispose()
    {
        foreach (string dir in _tempDirs)
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }

    // ── DeviceTypeString selection ──────────────────────────────────────────

    [Fact]
    public void DeviceTypeString_returns_NPU_when_cpu_proxy_is_disabled_and_openvino_is_available()
    {
        // Arrange: OpenVINO installed, proxy mode OFF
        var bootstrapper = CreateBootstrapper(
            isInstalled: true,
            installPath: CreateTempOpenVinoPath(),
            useOpenVinoCpuProxy: false);

        // Assert: DeviceTypeString should be "NPU" for real NPU targeting
        Assert.Equal("NPU", bootstrapper.DeviceTypeString);
    }

    [Fact]
    public void DeviceTypeString_returns_CPU_when_cpu_proxy_mode_is_active()
    {
        // Arrange: OpenVINO installed, proxy mode ON
        var bootstrapper = CreateBootstrapper(
            isInstalled: true,
            installPath: CreateTempOpenVinoPath(),
            useOpenVinoCpuProxy: true);

        // Assert: DeviceTypeString should be "CPU" for developer proxy mode
        Assert.Equal("CPU", bootstrapper.DeviceTypeString);
    }

    [Fact]
    public void DeviceTypeString_defaults_to_NPU_when_proxy_flag_is_false()
    {
        // Arrange: Explicitly set proxy to false (the default production behavior)
        var bootstrapper = CreateBootstrapper(
            isInstalled: true,
            installPath: CreateTempOpenVinoPath(),
            useOpenVinoCpuProxy: false);

        // Assert: Default behavior is NPU targeting
        Assert.False(bootstrapper.UseOpenVinoCpuProxy);
        Assert.Equal("NPU", bootstrapper.DeviceTypeString);
    }

    // ── CPU proxy mode activation ──────────────────────────────────────────

    [Fact]
    public void UseOpenVinoCpuProxy_is_true_when_proxy_flag_is_set()
    {
        var bootstrapper = CreateBootstrapper(
            isInstalled: true,
            installPath: CreateTempOpenVinoPath(),
            useOpenVinoCpuProxy: true);

        Assert.True(bootstrapper.UseOpenVinoCpuProxy);
    }

    [Fact]
    public void UseOpenVinoCpuProxy_is_false_when_proxy_flag_is_not_set()
    {
        var bootstrapper = CreateBootstrapper(
            isInstalled: true,
            installPath: CreateTempOpenVinoPath(),
            useOpenVinoCpuProxy: false);

        Assert.False(bootstrapper.UseOpenVinoCpuProxy);
    }

    // ── Fallback when OpenVINO not installed ────────────────────────────────

    [Fact]
    public void IsAvailable_is_false_when_component_is_not_installed()
    {
        // Arrange: Component not installed in the store
        var bootstrapper = CreateBootstrapper(
            isInstalled: false,
            installPath: null,
            useOpenVinoCpuProxy: false);

        // Assert: Bootstrapper reports unavailable
        Assert.False(bootstrapper.IsAvailable);
    }

    [Fact]
    public void IsAvailable_is_false_when_install_path_is_null()
    {
        // Arrange: Component marked as installed but path is null
        var bootstrapper = CreateBootstrapper(
            isInstalled: true,
            installPath: null,
            useOpenVinoCpuProxy: false);

        Assert.False(bootstrapper.IsAvailable);
    }

    [Fact]
    public void IsAvailable_is_false_when_native_library_does_not_exist_at_path()
    {
        // Arrange: Component installed but native DLL not present at path
        string emptyDir = Path.Combine(Path.GetTempPath(), $"openvino_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);
        try
        {
            var bootstrapper = CreateBootstrapper(
                isInstalled: true,
                installPath: emptyDir,
                useOpenVinoCpuProxy: false);

            Assert.False(bootstrapper.IsAvailable);
        }
        finally
        {
            Directory.Delete(emptyDir, recursive: true);
        }
    }

    // ── EP Discovery reports availability based on hardware profile ──────────

    [Fact]
    public async Task Discovery_reports_DirectMl_unavailable_when_gpu_is_not_present()
    {
        // Arrange: Windows host without GPU
        var discovery = new OnnxExecutionProviderDiscovery(new NullOpenVinoAvailabilityProvider());
        var profile = new HardwareProfile("windows", "x64", HasGpu: false, GpuDescription: null);

        // Act
        IReadOnlyList<ExecutionProviderAvailability> result = await discovery.DiscoverAsync(profile);

        // Assert
        ExecutionProviderAvailability directMlEntry = result.Single(r => r.Provider == ExecutionProviderKind.DirectMl);
        Assert.False(directMlEntry.IsAvailable);
    }

    [Fact]
    public async Task Discovery_reports_DirectMl_available_when_gpu_is_present_on_windows()
    {
        // Arrange: Windows host with GPU
        var discovery = new OnnxExecutionProviderDiscovery(new NullOpenVinoAvailabilityProvider());
        var profile = new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "Intel UHD Graphics 770");

        // Act
        IReadOnlyList<ExecutionProviderAvailability> result = await discovery.DiscoverAsync(profile);

        // Assert
        ExecutionProviderAvailability directMlEntry = result.Single(r => r.Provider == ExecutionProviderKind.DirectMl);
        Assert.True(directMlEntry.IsAvailable);
    }

    [Fact]
    public async Task Discovery_reports_TensorRt_unavailable_without_nvidia_gpu()
    {
        // Arrange: Windows host with non-NVIDIA GPU
        var discovery = new OnnxExecutionProviderDiscovery(new NullOpenVinoAvailabilityProvider());
        var profile = new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "Intel UHD Graphics 770");

        // Act
        IReadOnlyList<ExecutionProviderAvailability> result = await discovery.DiscoverAsync(profile);

        // Assert
        ExecutionProviderAvailability tensorRtEntry = result.Single(r => r.Provider == ExecutionProviderKind.TensorRTRtx);
        Assert.False(tensorRtEntry.IsAvailable);
    }

#if WINDOWS
    [Fact]
    public async Task WindowsDeviceEnumerator_does_not_advertise_npu_when_cpu_proxy_mode_is_enabled()
    {
        var provider = new StubOpenVinoAvailabilityProvider(
            isAvailable: true,
            useOpenVinoCpuProxy: true);
        var enumerator = new WindowsDeviceEnumerator(
            provider,
            NullLogger<WindowsDeviceEnumerator>.Instance,
            hasIntelNpuPciDevice: () => true);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        Assert.DoesNotContain(devices, device => device.Kind == DeviceKind.Npu);
        DeviceEntry cpu = Assert.Single(devices, device => device.Kind == DeviceKind.Cpu);
        Assert.Contains(ExecutionProviderKind.OpenVino, cpu.SupportedProviders);
    }

    [Fact]
    public async Task WindowsDeviceEnumerator_assigns_unique_device_indexes_when_npu_is_advertised()
    {
        var provider = new StubOpenVinoAvailabilityProvider(
            isAvailable: true,
            useOpenVinoCpuProxy: false);
        var enumerator = new WindowsDeviceEnumerator(
            provider,
            NullLogger<WindowsDeviceEnumerator>.Instance,
            hasIntelNpuPciDevice: () => true);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        Assert.Contains(devices, device => device.Kind == DeviceKind.Npu);
        Assert.Equal(devices.Count, devices.Select(device => device.DeviceIndex).Distinct().Count());
    }
#endif

    // ── Helpers ─────────────────────────────────────────────────────────────

    private static OpenVinoBootstrapper CreateBootstrapper(
        bool isInstalled,
        string? installPath,
        bool useOpenVinoCpuProxy)
    {
        return new OpenVinoBootstrapper(
            isComponentInstalled: _ => isInstalled,
            getComponentInstallPath: _ => installPath,
            useOpenVinoCpuProxy: useOpenVinoCpuProxy,
            logger: NullLogger<OpenVinoBootstrapper>.Instance);
    }

    /// <summary>
    /// Creates a temporary directory simulating an OpenVINO install path.
    /// The native library won't actually load, but this tests the path resolution logic.
    /// Note: IsAvailable will be false because the DLL doesn't exist, but DeviceTypeString
    /// is a pure property based on UseOpenVinoCpuProxy regardless of availability.
    /// </summary>
    private string CreateTempOpenVinoPath()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"openvino_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _tempDirs.Add(dir);
        return dir;
    }

#if WINDOWS
    private sealed class StubOpenVinoAvailabilityProvider : IOpenVinoAvailabilityProvider
    {
        public StubOpenVinoAvailabilityProvider(bool isAvailable, bool useOpenVinoCpuProxy)
        {
            IsAvailable = isAvailable;
            UseOpenVinoCpuProxy = useOpenVinoCpuProxy;
        }

        public bool IsAvailable { get; }

        public bool UseOpenVinoCpuProxy { get; }
    }
#endif
}
