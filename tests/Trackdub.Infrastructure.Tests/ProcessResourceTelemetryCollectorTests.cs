using System.Diagnostics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;
using Trackdub.Infrastructure.Diagnostics;

namespace Trackdub.Infrastructure.Tests;

public sealed class ProcessResourceTelemetryCollectorTests
{
    [Fact]
    public void Capture_reports_process_counters_monotonic_time_and_explicit_missing_vram_reader()
    {
        IResourceTelemetryCollector collector = new ProcessResourceTelemetryCollector();
        double before = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;
        ResourceUsageSnapshot start = collector.Capture();
        byte[] allocation = new byte[8192];
        ResourceUsageSnapshot end = collector.Capture();
        GC.KeepAlive(allocation);
        double after = Stopwatch.GetTimestamp() * 1000d / Stopwatch.Frequency;

        Assert.Equal(Environment.ProcessorCount, end.ProcessorCount);
        Assert.InRange(start.MonotonicMilliseconds!.Value, before, after);
        Assert.InRange(end.MonotonicMilliseconds!.Value, start.MonotonicMilliseconds.Value, after);
        Assert.True(end.ManagedAllocatedBytes >= start.ManagedAllocatedBytes);
        Assert.True(start.ManagedAllocatedBytes >= 0);
        AssertCounterOrReason(start.CpuTimeMilliseconds, start.CpuUnavailableReason);
        AssertCounterOrReason(end.CpuTimeMilliseconds, end.CpuUnavailableReason);
        AssertCounterOrReason(start.WorkingSetBytes, start.MemoryUnavailableReason);
        AssertCounterOrReason(end.WorkingSetBytes, end.MemoryUnavailableReason);
        if (start.CpuTimeMilliseconds.HasValue && end.CpuTimeMilliseconds.HasValue)
        {
            Assert.True(end.CpuTimeMilliseconds >= start.CpuTimeMilliseconds);
        }
        Assert.Null(end.AvailableVramMb);
        Assert.Equal("No VRAM reader is registered for this host.", end.VramUnavailableReason);
    }

    [Fact]
    public void Capture_uses_the_injected_vram_reader()
    {
        IResourceTelemetryCollector collector = new ProcessResourceTelemetryCollector(new FixedVramReader(8192));

        ResourceUsageSnapshot snapshot = collector.Capture();

        Assert.Equal(8192L, snapshot.AvailableVramMb);
        Assert.Null(snapshot.VramUnavailableReason);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(-4096)]
    public void Capture_degrades_a_negative_vram_reading_to_unavailable(long reading)
    {
        IResourceTelemetryCollector collector = new ProcessResourceTelemetryCollector(new FixedVramReader(reading));

        ResourceUsageSnapshot snapshot = collector.Capture();

        Assert.Null(snapshot.AvailableVramMb);
        Assert.Equal("VRAM reader returned a negative reading.", snapshot.VramUnavailableReason);
    }

    [Fact]
    public void Capture_degrades_a_throwing_vram_reader_without_failing_the_sample()
    {
        IResourceTelemetryCollector collector = new ProcessResourceTelemetryCollector(new ThrowingVramReader());

        ResourceUsageSnapshot snapshot = collector.Capture();

        Assert.Null(snapshot.AvailableVramMb);
        Assert.Contains("InvalidOperationException", snapshot.VramUnavailableReason, StringComparison.Ordinal);
        // The process counters must survive a broken GPU source.
        AssertCounterOrReason(snapshot.CpuTimeMilliseconds, snapshot.CpuUnavailableReason);
        Assert.True(snapshot.WorkingSetBytes > 0);
    }

    private sealed class FixedVramReader(long reading) : IAvailableVramReader
    {
        public long? ReadAvailableVramMb() => reading;

        public string UnavailableReason => "fixed";
    }

    private sealed class ThrowingVramReader : IAvailableVramReader
    {
        public long? ReadAvailableVramMb() => throw new InvalidOperationException("DXGI adapter busy.");

        public string UnavailableReason => "never reached";
    }

    private static void AssertCounterOrReason(double? value, string? reason)
    {
        if (value.HasValue)
        {
            Assert.True(double.IsFinite(value.Value));
            Assert.True(value.Value >= 0);
            Assert.Null(reason);
        }
        else
        {
            Assert.False(string.IsNullOrWhiteSpace(reason));
        }
    }
}
