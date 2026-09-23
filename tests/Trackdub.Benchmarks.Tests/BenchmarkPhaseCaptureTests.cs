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
}
