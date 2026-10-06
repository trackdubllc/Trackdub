using Trackdub.Benchmarks;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests;

/// <summary>
/// The startup banner reports the host's detected devices, its video memory and the effective
/// capacity a VRAM floor is checked against, so a bound's feasibility is visible before a run
/// starts.
/// </summary>
public sealed class HostCapacityBannerTests
{
    // ── Adapters and memory ─────────────────────────────────────────────────────────────────

    [Fact]
    public void Lists_each_device_with_its_own_memory()
    {
        string[] lines = [.. HostCapacityBanner.Describe(
            [
                Device(DeviceKind.DiscreteGpu, 0, "GeForce RTX 4090", "NVIDIA", 24564, 32496),
                Device(DeviceKind.IntegratedGpu, 1, "Intel UHD Graphics", "Intel", 128, 8192),
                Device(DeviceKind.Cpu, 2, "CPU", "System", 0, 0),
            ],
            new ResourceTelemetryBounds())];

        Assert.Contains(lines, line =>
            line.Contains("discrete GPU #0: GeForce RTX 4090 (NVIDIA)", StringComparison.Ordinal)
            && line.Contains("24564 MB dedicated + 32496 MB shared", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("integrated GPU #1: Intel UHD Graphics (Intel)", StringComparison.Ordinal)
            && line.Contains("128 MB dedicated + 8192 MB shared", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("CPU #2: CPU (System)", StringComparison.Ordinal)
            && line.Contains("no adapter memory", StringComparison.Ordinal));
    }

    [Fact]
    public void Separates_detected_video_memory_from_the_effective_capacity()
    {
        // The sum of both adapters is not the capacity: a floor applies to one adapter, so the
        // ceiling is the largest adapter's dedicated plus shared memory.
        string[] lines = [.. HostCapacityBanner.Describe(
            [
                Device(DeviceKind.DiscreteGpu, 0, "GeForce RTX 4090", "NVIDIA", 8192, 4096),
                Device(DeviceKind.IntegratedGpu, 1, "Intel UHD Graphics", "Intel", 128, 8192),
            ],
            new ResourceTelemetryBounds())];

        Assert.Contains(lines, line =>
            line.Contains("Detected video memory: 20608 MB across 2 GPU adapters", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("Effective VRAM capacity: 12288 MB", StringComparison.Ordinal));
    }

    [Fact]
    public void Counts_only_gpu_adapters_in_the_detected_total()
    {
        // An NPU's working set is a device-local estimate rather than adapter memory, so it must
        // not inflate the total the run's telemetry can actually observe.
        string[] lines = [.. HostCapacityBanner.Describe(
            [
                Device(DeviceKind.DiscreteGpu, 0, "GeForce RTX 4090", "NVIDIA", 8192, 4096),
                Device(DeviceKind.Npu, 1, "Intel NPU", "Intel", 50, 0),
                Device(DeviceKind.Cpu, 2, "CPU", "System", 0, 0),
            ],
            new ResourceTelemetryBounds())];

        Assert.Contains(lines, line =>
            line.Contains("Detected video memory: 12288 MB across 1 GPU adapter", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("Effective VRAM capacity: 12288 MB", StringComparison.Ordinal));
    }

    // ── Unknown capacity ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Reports_unknown_capacity_when_the_host_cannot_be_enumerated()
    {
        string[] lines = [.. HostCapacityBanner.Describe(
            devices: null,
            new ResourceTelemetryBounds { MinAvailableVramMb = 8192 })];

        Assert.Contains(lines, line => line.Contains("Devices: unavailable", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("Effective VRAM capacity: unknown", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("cannot be checked", StringComparison.Ordinal)
            && line.Contains("--min-available-vram-mb 8192 MB", StringComparison.Ordinal));
    }

    [Fact]
    public void Reports_unknown_capacity_when_no_gpu_adapter_reports_memory()
    {
        // A CPU-only host enumerates successfully but still has no capacity to check a floor
        // against, which is how the run's own pre-flight reads it.
        string[] lines = [.. HostCapacityBanner.Describe(
            [Device(DeviceKind.Cpu, 0, "CPU", "System", 0, 0)],
            new ResourceTelemetryBounds { MinAvailableVramMb = 8192 })];

        Assert.Contains(lines, line => line.Contains("CPU #0", StringComparison.Ordinal));
        Assert.DoesNotContain(lines, line => line.Contains("Detected video memory", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("Effective VRAM capacity: unknown - no GPU adapter reported memory", StringComparison.Ordinal));
        Assert.Contains(lines, line =>
            line.Contains("Bound feasibility", StringComparison.Ordinal)
            && line.Contains("cannot be checked", StringComparison.Ordinal));
    }

    // ── Bound feasibility ───────────────────────────────────────────────────────────────────

    [Fact]
    public void Flags_a_floor_above_the_effective_capacity_as_unreachable()
    {
        string[] lines = [.. HostCapacityBanner.Describe(
            [Device(DeviceKind.DiscreteGpu, 0, "GeForce RTX 4090", "NVIDIA", 8192, 4096)],
            new ResourceTelemetryBounds { MinAvailableVramMb = 24576 })];

        string feasibility = Assert.Single(lines, line => line.Contains("Bound feasibility", StringComparison.Ordinal));
        Assert.Contains("--min-available-vram-mb 24576", feasibility, StringComparison.Ordinal);
        Assert.Contains("12288 MB", feasibility, StringComparison.Ordinal);
        Assert.Contains("no run could leave that much VRAM headroom", feasibility, StringComparison.Ordinal);
    }

    [Fact]
    public void Accepts_a_floor_within_the_effective_capacity()
    {
        string[] lines = [.. HostCapacityBanner.Describe(
            [Device(DeviceKind.DiscreteGpu, 0, "GeForce RTX 4090", "NVIDIA", 8192, 4096)],
            new ResourceTelemetryBounds { MinAvailableVramMb = 12288 })];

        string feasibility = Assert.Single(lines, line => line.Contains("Bound feasibility", StringComparison.Ordinal));
        Assert.Contains("--min-available-vram-mb 12288 MB is within", feasibility, StringComparison.Ordinal);
    }

    [Fact]
    public void Notes_when_no_floor_is_configured()
    {
        string[] lines = [.. HostCapacityBanner.Describe(
            [Device(DeviceKind.DiscreteGpu, 0, "GeForce RTX 4090", "NVIDIA", 8192, 4096)],
            new ResourceTelemetryBounds())];

        Assert.Contains(lines, line =>
            line.Contains("no --min-available-vram-mb floor configured", StringComparison.Ordinal));
    }

    [Fact]
    public void Null_bounds_are_rejected_as_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(
            () => HostCapacityBanner.Describe([Device(DeviceKind.Cpu, 0, "CPU", "System", 0, 0)], null!));
    }

    // ── Banner wiring ───────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WriteAsync_reports_the_host_through_the_shared_device_enumeration()
    {
        using var output = new StringWriter();

        await HostCapacityBanner.WriteAsync(
            output, new ResourceTelemetryBounds(), TestContext.Current.CancellationToken);

        string text = output.ToString();
        Assert.Contains("Host capacity", text, StringComparison.Ordinal);
        Assert.Contains("Effective VRAM capacity", text, StringComparison.Ordinal);
        // The capacity line is never omitted, whatever this host can enumerate.
        Assert.Contains("Bound feasibility", text, StringComparison.Ordinal);
    }

    private static DeviceEntry Device(
        DeviceKind kind, int index, string description, string vendor, int dedicatedMb, int sharedMb) =>
        new(
            Kind: kind,
            DeviceIndex: index,
            AdapterDescription: description,
            VendorName: vendor,
            DedicatedVramMb: dedicatedMb,
            SharedMemoryMb: sharedMb,
            SupportedProviders: [ExecutionProviderKind.Cpu]);
}
