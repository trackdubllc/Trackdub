using System.Runtime.Versioning;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Dnnl;
using Trackdub.Inference.Onnx.Runtime;
using Microsoft.Extensions.Logging.Abstractions;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Smoke tests for MacDeviceEnumerator on a real macOS build agent.
/// Compiled on macOS only (Compile Remove'd in the test csproj for other OSes).
/// These tests run against actual Metal/system_profiler — no mocking of native calls.
/// </summary>
[SupportedOSPlatform("macos10.15")]
public sealed class MacDeviceEnumeratorTests
{
    // ── Basic contract on real macOS hardware ────────────────────────────────

    [Fact]
    public async Task GetDevicesAsync_always_returns_at_least_cpu_entry()
    {
        var enumerator = new MacDeviceEnumerator(NullLogger<MacDeviceEnumerator>.Instance);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        DeviceEntry cpu = Assert.Single(devices, d => d.Kind == DeviceKind.Cpu);

        // Dnnl is only advertised when x64 AND the loaded ONNX Runtime actually lists
        // DnnlExecutionProvider (real DNNL native assets on the test agent). Default
        // `dotnet test` runs without those assets, so assert against the same condition
        // the enumerator itself checks rather than assuming Dnnl is always present.
        bool expectDnnl =
            System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture
                == System.Runtime.InteropServices.Architecture.X64
            && DnnlOrtProbe.IsProviderListed();
        if (expectDnnl)
        {
            Assert.Contains(ExecutionProviderKind.Dnnl, cpu.SupportedProviders);
        }
        else
        {
            Assert.DoesNotContain(ExecutionProviderKind.Dnnl, cpu.SupportedProviders);
        }
    }

    [Fact]
    public async Task GetDevicesAsync_every_non_cpu_device_has_CoreMl_in_providers()
    {
        var enumerator = new MacDeviceEnumerator(NullLogger<MacDeviceEnumerator>.Instance);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        foreach (DeviceEntry device in devices.Where(d => d.Kind != DeviceKind.Cpu))
        {
            Assert.Contains(ExecutionProviderKind.CoreMl, device.SupportedProviders);
        }
    }

    [Fact]
    public async Task GetDevicesAsync_returns_same_list_on_second_call()
    {
        var enumerator = new MacDeviceEnumerator(NullLogger<MacDeviceEnumerator>.Instance);

        IReadOnlyList<DeviceEntry> first = await enumerator.GetDevicesAsync();
        IReadOnlyList<DeviceEntry> second = await enumerator.GetDevicesAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task GetDevicesAsync_device_indexes_are_unique()
    {
        var enumerator = new MacDeviceEnumerator(NullLogger<MacDeviceEnumerator>.Instance);

        IReadOnlyList<DeviceEntry> devices = await enumerator.GetDevicesAsync();

        int distinctIndexCount = devices.Select(d => d.DeviceIndex).Distinct().Count();
        Assert.Equal(devices.Count, distinctIndexCount);
    }
}
