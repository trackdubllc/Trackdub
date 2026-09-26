using Trackdub.Benchmarks.Metrics;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class ResourceTelemetryTests
{
    // =========================================================================
    // CaptureProcess() Tests
    // =========================================================================

    [Fact]
    public void CaptureProcess_ReturnsPlausibleProcessMetrics()
    {
        ResourceTelemetrySnapshot snapshot = ResourceTelemetry.CaptureProcess();

        Assert.NotNull(snapshot);
        Assert.True(snapshot.WorkingSetBytes > 0, "Working set bytes must be greater than zero.");
        Assert.True(snapshot.PeakWorkingSetBytes > 0, "Peak working set bytes must be greater than zero.");
        Assert.True(snapshot.PeakWorkingSetBytes >= snapshot.WorkingSetBytes,
            $"Peak working set ({snapshot.PeakWorkingSetBytes}) must be >= current working set ({snapshot.WorkingSetBytes}).");
        Assert.True(snapshot.ManagedAllocatedBytes > 0, "Managed allocated bytes must be greater than zero.");
        Assert.True(snapshot.Gen0Collections >= 0, "Gen 0 collections must be non-negative.");
        Assert.True(snapshot.Gen1Collections >= 0, "Gen 1 collections must be non-negative.");
        Assert.True(snapshot.Gen2Collections >= 0, "Gen 2 collections must be non-negative.");
    }

    [Fact]
    public void CaptureProcess_RespectsGenerationalGcOrdering()
    {
        ResourceTelemetrySnapshot snapshot = ResourceTelemetry.CaptureProcess();

        // In .NET GC, Gen 0 collections encompass Gen 1, and Gen 1 encompasses Gen 2
        Assert.True(snapshot.Gen0Collections >= snapshot.Gen1Collections,
            $"Gen 0 collections ({snapshot.Gen0Collections}) must be >= Gen 1 collections ({snapshot.Gen1Collections}).");
        Assert.True(snapshot.Gen1Collections >= snapshot.Gen2Collections,
            $"Gen 1 collections ({snapshot.Gen1Collections}) must be >= Gen 2 collections ({snapshot.Gen2Collections}).");
    }

    [Fact]
    public void CaptureProcess_ReflectsNewManagedAllocations()
    {
        ResourceTelemetrySnapshot start = ResourceTelemetry.CaptureProcess();

        // Allocate a 2 MB managed buffer and touch it to ensure allocation
        const int allocBytes = 2 * 1024 * 1024;
        byte[] buffer = new byte[allocBytes];
        Random.Shared.NextBytes(buffer);

        ResourceTelemetrySnapshot end = ResourceTelemetry.CaptureProcess();

        long diff = end.ManagedAllocatedBytes - start.ManagedAllocatedBytes;
        Assert.True(diff >= allocBytes,
            $"Managed allocations ({diff} bytes) should reflect the allocated buffer of at least {allocBytes} bytes.");
    }

    // =========================================================================
    // CalculateDelta(start, end) Tests
    // =========================================================================

    [Fact]
    public void CalculateDelta_StandardProgressiveWorkload_ComputesExactDeltas()
    {
        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 100_000_000,
            PeakWorkingSetBytes: 120_000_000,
            ManagedAllocatedBytes: 50_000_000,
            Gen0Collections: 10,
            Gen1Collections: 3,
            Gen2Collections: 1);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 160_000_000,
            PeakWorkingSetBytes: 180_000_000,
            ManagedAllocatedBytes: 95_000_000,
            Gen0Collections: 16,
            Gen1Collections: 5,
            Gen2Collections: 2);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(60_000_000, delta.WorkingSetDeltaBytes);
        Assert.Equal(160_000_000, delta.PeakWorkingSetBytes);
        Assert.Equal(45_000_000, delta.ManagedAllocatedBytes);
        Assert.Equal(6, delta.Gen0Collections);
        Assert.Equal(2, delta.Gen1Collections);
        Assert.Equal(1, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_IdenticalSnapshots_ProducesZeroDelta()
    {
        var snapshot = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 200_000_000,
            PeakWorkingSetBytes: 250_000_000,
            ManagedAllocatedBytes: 100_000_000,
            Gen0Collections: 20,
            Gen1Collections: 8,
            Gen2Collections: 2);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(snapshot, snapshot);

        Assert.Equal(0, delta.WorkingSetDeltaBytes);
        Assert.Equal(200_000_000, delta.PeakWorkingSetBytes);
        Assert.Equal(0, delta.ManagedAllocatedBytes);
        Assert.Equal(0, delta.Gen0Collections);
        Assert.Equal(0, delta.Gen1Collections);
        Assert.Equal(0, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_NegativeWorkingSetDelta_CapturesMemoryTrimmingCorrectly()
    {
        // When the OS trims working set or large native allocations are released
        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 500_000_000,
            PeakWorkingSetBytes: 550_000_000,
            ManagedAllocatedBytes: 100_000_000,
            Gen0Collections: 5,
            Gen1Collections: 1,
            Gen2Collections: 0);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 350_000_000,
            PeakWorkingSetBytes: 550_000_000,
            ManagedAllocatedBytes: 110_000_000,
            Gen0Collections: 6,
            Gen1Collections: 2,
            Gen2Collections: 1);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(-150_000_000, delta.WorkingSetDeltaBytes);
        Assert.Equal(500_000_000, delta.PeakWorkingSetBytes);
        Assert.Equal(10_000_000, delta.ManagedAllocatedBytes);
        Assert.Equal(1, delta.Gen0Collections);
        Assert.Equal(1, delta.Gen1Collections);
        Assert.Equal(1, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_PeakWorkingSet_UsesHigherOfBoundaryWorkingSets()
    {
        // The delta's PeakWorkingSetBytes is an endpoint-sampled max of the two snapshots'
        // WorkingSetBytes, not the snapshots' own (process-lifetime) PeakWorkingSetBytes field.

        // Scenario 1: End has higher working set
        var s1 = new ResourceTelemetrySnapshot(100, 150, 50, 0, 0, 0);
        var e1 = new ResourceTelemetrySnapshot(120, 200, 60, 0, 0, 0);
        Assert.Equal(120, ResourceTelemetry.CalculateDelta(s1, e1).PeakWorkingSetBytes);

        // Scenario 2: Start has higher working set
        var s2 = new ResourceTelemetrySnapshot(130, 300, 50, 0, 0, 0);
        var e2 = new ResourceTelemetrySnapshot(120, 200, 60, 0, 0, 0);
        Assert.Equal(130, ResourceTelemetry.CalculateDelta(s2, e2).PeakWorkingSetBytes);
    }

    [Fact]
    public void CalculateDelta_MultiGigabyteValues_NoInt32Overflow()
    {
        long sixteenGb = 16L * 1024 * 1024 * 1024;
        long twentyFourGb = 24L * 1024 * 1024 * 1024;
        long eightGb = 8L * 1024 * 1024 * 1024;

        var start = new ResourceTelemetrySnapshot(sixteenGb, sixteenGb, sixteenGb, 100, 20, 5);
        var end = new ResourceTelemetrySnapshot(twentyFourGb, twentyFourGb, twentyFourGb, 150, 30, 8);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(eightGb, delta.WorkingSetDeltaBytes);
        Assert.Equal(twentyFourGb, delta.PeakWorkingSetBytes);
        Assert.Equal(eightGb, delta.ManagedAllocatedBytes);
        Assert.Equal(50, delta.Gen0Collections);
        Assert.Equal(10, delta.Gen1Collections);
        Assert.Equal(3, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_ClampsNegativeAllocationsAndGcCountsToZero()
    {
        // Synthetic anomalous snapshots where end < start
        var start = new ResourceTelemetrySnapshot(100, 100, 500, 10, 5, 2);
        var end = new ResourceTelemetrySnapshot(100, 100, 200, 8, 3, 1);

        ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(start, end);

        Assert.Equal(0, delta.ManagedAllocatedBytes);
        Assert.Equal(0, delta.Gen0Collections);
        Assert.Equal(0, delta.Gen1Collections);
        Assert.Equal(0, delta.Gen2Collections);
    }

    [Fact]
    public void CalculateDelta_NullStartOrEnd_ThrowsArgumentNullException()
    {
        var valid = new ResourceTelemetrySnapshot(100, 100, 100, 0, 0, 0);

        Assert.Throws<ArgumentNullException>(() => ResourceTelemetry.CalculateDelta(null!, valid));
        Assert.Throws<ArgumentNullException>(() => ResourceTelemetry.CalculateDelta(valid, null!));
    }

    // =========================================================================
    // Record Equality & Singleton Tests
    // =========================================================================

    [Fact]
    public void ResourceTelemetrySnapshot_ValueEqualityAndEmptySingleton()
    {
        var a = new ResourceTelemetrySnapshot(100, 200, 300, 1, 2, 3);
        var b = new ResourceTelemetrySnapshot(100, 200, 300, 1, 2, 3);
        var c = new ResourceTelemetrySnapshot(100, 200, 301, 1, 2, 3);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.NotEqual(a, c);

        Assert.Equal(0, ResourceTelemetrySnapshot.Empty.WorkingSetBytes);
        Assert.Equal(0, ResourceTelemetrySnapshot.Empty.PeakWorkingSetBytes);
        Assert.Equal(0, ResourceTelemetrySnapshot.Empty.ManagedAllocatedBytes);
    }

    [Fact]
    public void ResourceTelemetryDelta_ValueEqualityAndEmptySingleton()
    {
        var a = new ResourceTelemetryDelta(50, 200, 100, 1, 0, 0);
        var b = new ResourceTelemetryDelta(50, 200, 100, 1, 0, 0);
        var c = new ResourceTelemetryDelta(50, 200, 101, 1, 0, 0);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.NotEqual(a, c);

        Assert.Equal(0, ResourceTelemetryDelta.Empty.WorkingSetDeltaBytes);
        Assert.Equal(0, ResourceTelemetryDelta.Empty.PeakWorkingSetBytes);
        Assert.Equal(0, ResourceTelemetryDelta.Empty.ManagedAllocatedBytes);
    }
}
