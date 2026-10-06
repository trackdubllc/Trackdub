using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Benchmarks.Tests;

/// <summary>
/// Pre-flight sanitization of resource bounds: a bound the host's adapters can never satisfy is
/// rejected before any iteration is measured instead of being accepted and reported later.
/// </summary>
public sealed class ResourceBoundsPreflightTests
{
    // ── Rule ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(0L, 1L)]                 // capacity unknown — never guess
    [InlineData(0L, long.MaxValue)]
    [InlineData(8192L, null)]            // no floor configured
    [InlineData(8192L, 500L)]
    [InlineData(8192L, 8192L)]           // exactly the ceiling is still reachable on an idle adapter
    [InlineData(12288L, 12288L)]
    public void Satisfiable_or_unknown_floors_are_accepted(long capacityMb, long? floorMb)
    {
        var bounds = new ResourceTelemetryBounds { MinAvailableVramMb = floorMb };

        Assert.Null(ResourceBoundsPreflight.DescribeImpossibleBound(bounds, capacityMb));
    }

    [Fact]
    public void Floor_above_total_video_memory_is_rejected_with_the_option_and_both_values()
    {
        var bounds = new ResourceTelemetryBounds { MinAvailableVramMb = 24576 };

        string? reason = ResourceBoundsPreflight.DescribeImpossibleBound(bounds, 12288);

        Assert.NotNull(reason);
        Assert.Contains("--min-available-vram-mb 24576", reason, StringComparison.Ordinal);
        Assert.Contains("12288 MB", reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Vacuous_maxima_are_accepted_because_every_reading_satisfies_them()
    {
        // A ceiling above capacity is unbounded rather than impossible, so it stays accepted;
        // only a minimum can be unsatisfiable on its own.
        var bounds = new ResourceTelemetryBounds
        {
            MaxGpuBytes = long.MaxValue,
            MaxWorkingSetBytes = long.MaxValue,
            MaxManagedAllocatedBytes = long.MaxValue,
            MaxCpuPercent = 100,
        };

        Assert.Null(ResourceBoundsPreflight.DescribeImpossibleBound(bounds, 1024));
    }

    [Fact]
    public void Null_bounds_are_rejected_as_a_programming_error()
    {
        Assert.Throws<ArgumentNullException>(
            () => ResourceBoundsPreflight.DescribeImpossibleBound(null!, 1024));
    }

    // ── Capacity discovery ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Capacity_is_the_sampled_adapter_local_segment_memory()
    {
        using ServiceProvider provider = Provider(new StubDeviceEnumerator(
        [
            Device(DeviceKind.DiscreteGpu, index: 0, dedicatedMb: 8192, sharedMb: 4096),
            Device(DeviceKind.IntegratedGpu, index: 1, dedicatedMb: 128, sharedMb: 8192),
            Device(DeviceKind.Npu, index: 2, dedicatedMb: 50, sharedMb: 0),
            Device(DeviceKind.Cpu, index: 3, dedicatedMb: 0, sharedMb: 0),
        ]));

        // The telemetry floor is measured on the sampled adapter (device index 0), not on
        // whichever adapter is largest: a discrete adapter's local segment is its dedicated
        // VRAM, so shared memory and the larger integrated total must not inflate the ceiling.
        Assert.Equal(
            8192L,
            await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(provider, CancellationToken.None));
    }

    [Fact]
    public async Task Capacity_on_an_integrated_sampled_adapter_covers_shared_memory()
    {
        using ServiceProvider provider = Provider(new StubDeviceEnumerator(
        [
            Device(DeviceKind.IntegratedGpu, index: 0, dedicatedMb: 128, sharedMb: 8192),
            Device(DeviceKind.DiscreteGpu, index: 1, dedicatedMb: 8192, sharedMb: 4096),
        ]));

        // An integrated adapter's local segment spans the shared system memory it addresses,
        // so a floor above dedicated VRAM alone is still satisfiable there — and the larger
        // discrete adapter on index 1 must not shrink the ceiling either.
        Assert.Equal(
            8320L,
            await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(provider, CancellationToken.None));
    }

    [Fact]
    public async Task Capacity_is_unknown_when_the_sampled_adapter_is_absent()
    {
        // Telemetry is sampled from device index 0: a host enumerating only some other GPU
        // leaves the capacity unknown instead of borrowing an adapter the run never measures.
        // A 2048 MB adapter at index 1 must not reject a 4096 MB floor meant for index 0.
        using ServiceProvider provider = Provider(new StubDeviceEnumerator(
        [
            Device(DeviceKind.DiscreteGpu, index: 1, dedicatedMb: 2048, sharedMb: 0),
        ]));

        Assert.Equal(
            0L,
            await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(provider, CancellationToken.None));
    }

    [Fact]
    public async Task Capacity_is_unknown_without_an_enumerator_or_without_a_gpu_adapter()
    {
        using ServiceProvider empty = new ServiceCollection().BuildServiceProvider();
        Assert.Equal(0L, await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(empty, CancellationToken.None));
        Assert.Equal(0L, await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(null, CancellationToken.None));

        using ServiceProvider cpuOnly = Provider(new StubDeviceEnumerator([Device(DeviceKind.Cpu, 0, 0)]));
        Assert.Equal(0L, await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(cpuOnly, CancellationToken.None));
    }

    [Fact]
    public async Task Capacity_enumeration_failure_leaves_the_capacity_unknown()
    {
        using ServiceProvider provider = Provider(new StubDeviceEnumerator(devices: null));

        Assert.Equal(
            0L,
            await ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(provider, CancellationToken.None));
    }

    [Fact]
    public async Task Capacity_enumeration_honours_cancellation()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        using ServiceProvider provider = Provider(new StubDeviceEnumerator([]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => ResourceBoundsPreflight.QueryTotalVideoMemoryMbAsync(provider, cts.Token));
    }

    // ── Run integration ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Impossible_vram_floor_fails_the_run_before_any_measurement()
    {
        string directory = Directory.CreateTempSubdirectory("trackdub-impossible-bound-").FullName;
        try
        {
            string fixture = Path.Join(directory, "fixture.wav");
            await File.WriteAllBytesAsync(fixture, [1, 2, 3]);

            using var runner = RunnerWithCapacity(dedicatedMb: 1024, sharedMb: 0);
            BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
                ResourceTelemetryBounds = new ResourceTelemetryBounds { MinAvailableVramMb = 4096 },
            });

            Assert.Equal(BenchmarkEvidenceStatus.Failed, report.Status);
            Assert.Contains("--min-available-vram-mb 4096", report.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.Contains("1024 MB", report.Reason ?? string.Empty, StringComparison.Ordinal);

            // Rejected during host setup: no iteration ran, so nothing was measured (the timing
            // keys exist for every report, but a measured run is what fills them in) and the bound
            // was never reported as if the hardware could have met it.
            Assert.Empty(report.Stages);
            Assert.Null(report.TimingsMilliseconds["pipeline"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Floor_within_the_host_capacity_is_not_rejected()
    {
        string directory = Directory.CreateTempSubdirectory("trackdub-possible-bound-").FullName;
        try
        {
            string fixture = Path.Join(directory, "fixture.wav");
            await File.WriteAllBytesAsync(fixture, [1, 2, 3]);

            using var runner = RunnerWithCapacity(dedicatedMb: 8192, sharedMb: 4096);
            BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
                ResourceTelemetryBounds = new ResourceTelemetryBounds { MinAvailableVramMb = 4096 },
            });

            // The run is measured as usual (its own resource validation may still fail against
            // live readings — that is a measurement, not a rejected invocation).
            Assert.DoesNotContain(
                "--min-available-vram-mb", report.Reason ?? string.Empty, StringComparison.Ordinal);
            Assert.NotNull(report.TimingsMilliseconds["pipeline"]);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────

    /// <summary>Runner whose host reports a fixed adapter capacity, plus a mock pipeline.</summary>
    private static ControlledDubbingBenchmarkRunner RunnerWithCapacity(int dedicatedMb, int sharedMb) =>
        new(
            new NoHistory(),
            services =>
            {
                MockDubbingPipelineServices.ConfigureMockPipeline(services);
                services.Replace(ServiceDescriptor.Singleton<IDeviceEnumerator>(
                    new StubDeviceEnumerator([Device(DeviceKind.DiscreteGpu, dedicatedMb, sharedMb)])));
            });

    private static ServiceProvider Provider(IDeviceEnumerator enumerator) =>
        new ServiceCollection().AddSingleton(enumerator).BuildServiceProvider();

    private static DeviceEntry Device(DeviceKind kind, int dedicatedMb, int sharedMb, int index = 0) =>
        new(kind, index, "Test Adapter", "Test Vendor", dedicatedMb, sharedMb, [ExecutionProviderKind.DirectMl]);

    private sealed class StubDeviceEnumerator(IReadOnlyList<DeviceEntry>? devices) : IDeviceEnumerator
    {
        public Task<IReadOnlyList<DeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return devices is null
                ? Task.FromException<IReadOnlyList<DeviceEntry>>(new InvalidOperationException("Adapter enumeration failed."))
                : Task.FromResult(devices);
        }

        public Task<IReadOnlyList<DeviceEntry>> ReEnumerateAsync(CancellationToken cancellationToken = default) =>
            GetDevicesAsync(cancellationToken);
    }

    private sealed class NoHistory : IBenchmarkEvidenceRepository
    {
        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BenchmarkEvidenceReport?>(null);

        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
            BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>([]);
    }
}
