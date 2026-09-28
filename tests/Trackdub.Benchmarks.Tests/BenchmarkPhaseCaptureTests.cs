using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests;

public sealed class BenchmarkPhaseCaptureTests
{
    [Fact]
    public async Task CaptureScopesPhasesAndRestoresAmbientContext()
    {
        var first = new BenchmarkPhaseCapture();
        var second = new BenchmarkPhaseCapture();
        using (BenchmarkPhaseCapture.Activate(first))
        {
            using (BenchmarkPhaseCapture.Start("inference"))
                await Task.Delay(2);
            using (BenchmarkPhaseCapture.Activate(second))
            using (BenchmarkPhaseCapture.Start("session-create"))
                await Task.Delay(2);
            using (BenchmarkPhaseCapture.Start("inference"))
                await Task.Delay(2);
        }
        using (BenchmarkPhaseCapture.Start("unscoped"))
            await Task.Delay(1);

        Assert.True(first.SnapshotMilliseconds()["phase:inference"] > 0);
        Assert.DoesNotContain("phase:session-create", first.SnapshotMilliseconds().Keys);
        Assert.True(second.SnapshotMilliseconds()["phase:session-create"] > 0);
        Assert.DoesNotContain("phase:unscoped", first.SnapshotMilliseconds().Keys);
    }

    [Fact]
    public void CountersAndMaxima_AreAmbientScopedAndReturnRawNames()
    {
        var first = new BenchmarkPhaseCapture();
        var second = new BenchmarkPhaseCapture();

        using (BenchmarkPhaseCapture.Activate(first))
        {
            BenchmarkPhaseCapture.Increment("poolHit");
            BenchmarkPhaseCapture.Increment("poolHit", 2);
            BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 3);
            BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 1); // lower must not overwrite
            using (BenchmarkPhaseCapture.Activate(second))
            {
                BenchmarkPhaseCapture.Increment("poolHit", 10);
                BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 7);
            }
        }

        // Outside any ambient scope these are no-ops.
        BenchmarkPhaseCapture.Increment("poolHit");
        BenchmarkPhaseCapture.ObserveMaximum("admissionWaiters", 99);

        Assert.Equal(3, first.SnapshotCounters()["poolHit"]);
        Assert.Equal(3, first.SnapshotMaxima()["admissionWaiters"]);
        Assert.Equal(10, second.SnapshotCounters()["poolHit"]);
        Assert.Equal(7, second.SnapshotMaxima()["admissionWaiters"]);
        Assert.DoesNotContain("poolHit", first.SnapshotMilliseconds().Keys);
    }

    [Fact]
    public void CountersAndMaxima_AreConcurrentSafe()
    {
        var capture = new BenchmarkPhaseCapture();
        const int tasks = 8;
        const int iterations = 500;

        using (BenchmarkPhaseCapture.Activate(capture))
        {
            Parallel.For(0, tasks, _ =>
            {
                for (int i = 0; i < iterations; i++)
                {
                    BenchmarkPhaseCapture.Increment("poolHit");
                    BenchmarkPhaseCapture.ObserveMaximum("pendingReservationMb", i);
                }
            });
        }

        Assert.Equal(tasks * iterations, capture.SnapshotCounters()["poolHit"]);
        Assert.Equal(iterations - 1, capture.SnapshotMaxima()["pendingReservationMb"]);
    }

    [Fact]
    public void CountersAndMaxima_ValidateArgumentsEvenWithoutAmbientCapture()
    {
        Assert.Throws<ArgumentException>(() => BenchmarkPhaseCapture.Increment(" "));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkPhaseCapture.Increment("x", -1));
        Assert.Throws<ArgumentException>(() => BenchmarkPhaseCapture.ObserveMaximum("", 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => BenchmarkPhaseCapture.ObserveMaximum("x", -5));
    }
}
