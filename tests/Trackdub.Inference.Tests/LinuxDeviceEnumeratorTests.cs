using System.Runtime.Versioning;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.Extensions.Logging.Abstractions;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Unit tests for LinuxDeviceEnumerator using a fake ISysfsReader so no real sysfs is touched.
/// Compiled on Linux only (Compile Remove'd in the test csproj for other OSes).
/// </summary>
[SupportedOSPlatform("linux")]
public sealed class LinuxDeviceEnumeratorTests
{
    // Dnnl is only advertised on the CPU device when the process is x64 AND the loaded ONNX
    // Runtime actually lists DnnlExecutionProvider (real DNNL native assets on the test agent).
    // Default `dotnet test` runs without those assets, so assert against the same condition
    // the enumerator itself checks rather than assuming Dnnl is always present.
    private static bool ExpectDnnl =>
        System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
            == System.Runtime.InteropServices.Architecture.X64
        && DnnlOrtProbe.IsProviderListed();

    // ── CPU always present ───────────────────────────────────────────────────

    [Fact]
    public async Task Cpu_always_present_even_when_no_pci_gpus()
    {
        var sysfs = new FakeSysfsReader();
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry cpu = Assert.Single(devices, d => d.Kind == DeviceKind.Cpu);
        AssertDnnlPresence(cpu);
    }

    [Fact]
    public async Task Cpu_always_present_alongside_gpu()
    {
        var sysfs = new FakeSysfsReader()
            .AddNvidiaGpu("0000:01:00.0");
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry cpu = Assert.Single(devices, d => d.Kind == DeviceKind.Cpu);
        AssertDnnlPresence(cpu);
    }

    private static void AssertDnnlPresence(DeviceEntry cpu)
    {
        if (ExpectDnnl)
        {
            Assert.Contains(ExecutionProviderKind.Dnnl, cpu.SupportedProviders);
        }
        else
        {
            Assert.DoesNotContain(ExecutionProviderKind.Dnnl, cpu.SupportedProviders);
        }
    }

    // ── NVIDIA GPU detection ─────────────────────────────────────────────────

    [Fact]
    public async Task Nvidia_gpu_produces_entry_with_Cuda_and_TensorRt_providers()
    {
        var sysfs = new FakeSysfsReader()
            .AddNvidiaGpu("0000:01:00.0", vramMb: 8192);
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry gpu = Assert.Single(devices, d => d.Kind == DeviceKind.DiscreteGpu);
        Assert.Contains(ExecutionProviderKind.Cuda, gpu.SupportedProviders);
        Assert.Contains(ExecutionProviderKind.TensorRt, gpu.SupportedProviders);
        Assert.Equal("NVIDIA", gpu.VendorName);
        Assert.Equal(8192, gpu.DedicatedVramMb);
    }

    // ── AMD GPU detection ────────────────────────────────────────────────────

    [Fact]
    public async Task Amd_gpu_produces_entry_with_Cpu_provider_only()
    {
        var sysfs = new FakeSysfsReader()
            .AddAmdGpu("0000:02:00.0");
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry gpu = Assert.Single(devices, d => d.Kind == DeviceKind.DiscreteGpu);
        Assert.Equal([ExecutionProviderKind.Cpu], gpu.SupportedProviders);
        Assert.Equal("AMD", gpu.VendorName);
    }

    // ── Intel GPU detection ──────────────────────────────────────────────────

    [Fact]
    public async Task Intel_gpu_produces_entry_with_OpenVino_provider()
    {
        var sysfs = new FakeSysfsReader()
            .AddIntelGpu("0000:00:02.0");
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry gpu = Assert.Single(devices, d => d.Kind == DeviceKind.IntegratedGpu);
        Assert.Contains(ExecutionProviderKind.OpenVino, gpu.SupportedProviders);
        Assert.Equal("Intel", gpu.VendorName);
    }

    // ── Intel NPU detection ──────────────────────────────────────────────────

    [Fact]
    public async Task Intel_npu_entry_present_when_intel_vpu_driver_is_loaded()
    {
        var sysfs = new FakeSysfsReader()
            .AddIntelNpu("0000:00:0b.0");
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry npu = Assert.Single(devices, d => d.Kind == DeviceKind.Npu);
        Assert.Contains(ExecutionProviderKind.OpenVino, npu.SupportedProviders);
    }

    [Fact]
    public async Task Intel_npu_not_present_when_intel_vpu_driver_absent()
    {
        var sysfs = new FakeSysfsReader();
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        Assert.DoesNotContain(devices, d => d.Kind == DeviceKind.Npu);
    }

    // ── OpenVINO CPU proxy mode ──────────────────────────────────────────────

    [Fact]
    public async Task Cpu_entry_includes_OpenVino_when_proxy_mode_enabled()
    {
        var sysfs = new FakeSysfsReader();
        var enumerator = Make(sysfs, openVinoAvailable: true, useOpenVinoCpuProxy: true);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry cpu = Assert.Single(devices, d => d.Kind == DeviceKind.Cpu);
        Assert.Contains(ExecutionProviderKind.OpenVino, cpu.SupportedProviders);
        AssertDnnlPresence(cpu);
    }

    [Fact]
    public async Task Cpu_entry_does_not_include_OpenVino_when_proxy_mode_disabled()
    {
        var sysfs = new FakeSysfsReader();
        var enumerator = Make(sysfs, openVinoAvailable: true, useOpenVinoCpuProxy: false);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry cpu = Assert.Single(devices, d => d.Kind == DeviceKind.Cpu);
        Assert.DoesNotContain(ExecutionProviderKind.OpenVino, cpu.SupportedProviders);
        AssertDnnlPresence(cpu);
    }

    // ── Caching ──────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetDevicesAsync_returns_same_list_on_second_call()
    {
        var sysfs = new FakeSysfsReader().AddNvidiaGpu("0000:01:00.0");
        var enumerator = Make(sysfs, openVinoAvailable: false);

        IReadOnlyList<DeviceEntry> first = await enumerator.GetDevicesAsync();
        IReadOnlyList<DeviceEntry> second = await enumerator.GetDevicesAsync();

        Assert.Same(first, second);
    }

    // ── Helpers ──────────────────────────────────────────────────────────────

    private static LinuxDeviceEnumerator Make(
        FakeSysfsReader sysfs,
        bool openVinoAvailable,
        bool useOpenVinoCpuProxy = false) =>
        new(new StubOpenVinoProvider(openVinoAvailable, useOpenVinoCpuProxy),
            sysfs,
            NullLogger<LinuxDeviceEnumerator>.Instance);

    private sealed class StubOpenVinoProvider : IOpenVinoAvailabilityProvider
    {
        public StubOpenVinoProvider(bool isAvailable, bool useProxy)
        {
            IsAvailable = isAvailable;
            UseOpenVinoCpuProxy = useProxy;
        }

        public bool IsAvailable { get; }
        public bool UseOpenVinoCpuProxy { get; }
    }

    /// <summary>
    /// Fake ISysfsReader backed by in-memory dictionaries.
    /// Call AddNvidiaGpu/AddAmdGpu/AddIntelGpu/AddIntelNpu to configure the simulated hardware.
    /// </summary>
    private sealed class FakeSysfsReader : ISysfsReader
    {
        private const string PciBase = "/sys/bus/pci/devices";
        private const string NpuDriverBase = "/sys/bus/pci/drivers/intel_vpu";

        private readonly List<string> _pciDeviceDirs = [];
        private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
        private readonly List<string> _npuSymlinks = [];

        public FakeSysfsReader AddNvidiaGpu(string address, long vramMb = 4096)
        {
            string dir = $"{PciBase}/{address}";
            _pciDeviceDirs.Add(dir);
            _files[$"{dir}/class"] = "0x030000";
            _files[$"{dir}/vendor"] = "0x10de";
            _files[$"/proc/driver/nvidia/gpus/{address}/information"] =
                $"Model: NVIDIA GPU\nVideo Memory: {vramMb} MB\n";
            return this;
        }

        public FakeSysfsReader AddAmdGpu(string address)
        {
            string dir = $"{PciBase}/{address}";
            _pciDeviceDirs.Add(dir);
            _files[$"{dir}/class"] = "0x030200";
            _files[$"{dir}/vendor"] = "0x1002";
            return this;
        }

        public FakeSysfsReader AddIntelGpu(string address)
        {
            string dir = $"{PciBase}/{address}";
            _pciDeviceDirs.Add(dir);
            _files[$"{dir}/class"] = "0x030000";
            _files[$"{dir}/vendor"] = "0x8086";
            return this;
        }

        public FakeSysfsReader AddIntelNpu(string address)
        {
            _npuSymlinks.Add($"{NpuDriverBase}/{address}");
            return this;
        }

        public IEnumerable<string> EnumerateDirectories(string path)
        {
            if (path.Equals(PciBase, StringComparison.Ordinal))
                return _pciDeviceDirs;
            if (path.Equals(NpuDriverBase, StringComparison.Ordinal))
                return _npuSymlinks;
            return [];
        }

        public bool DirectoryExists(string path)
        {
            if (path.Equals(NpuDriverBase, StringComparison.Ordinal))
                return _npuSymlinks.Count > 0;
            return false;
        }

        public bool FileExists(string path) => _files.ContainsKey(path);

        public string? ReadAllText(string path) =>
            _files.TryGetValue(path, out string? value) ? value : null;
    }
}
