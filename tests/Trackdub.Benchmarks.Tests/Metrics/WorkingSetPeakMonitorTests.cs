using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class WorkingSetPeakMonitorTests
{
    [Fact]
    public async Task Stop_returns_peak_from_intermediate_or_terminal_samples()
    {
        var sampler = new SequenceSampler(100, 800, 1200);
        var monitor = new WorkingSetPeakMonitor(sampler, 100, TimeSpan.FromMilliseconds(5));

        await sampler.SecondSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long? peak = monitor.Stop();

        Assert.Equal(1200, peak);
        Assert.Null(monitor.UnavailableReason);
    }

    [Fact]
    public void Stop_returns_unavailable_if_a_read_fails()
    {
        var monitor = new WorkingSetPeakMonitor(new ThrowingSampler(), 100, TimeSpan.FromSeconds(1));

        long? peak = monitor.Stop();

        Assert.Null(peak);
        Assert.Contains("InvalidOperationException", monitor.UnavailableReason, StringComparison.Ordinal);
    }

    [Fact]
    public void Constructor_rejects_nonpositive_sampling_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new WorkingSetPeakMonitor(new SequenceSampler(1), 1, TimeSpan.Zero));
    }

    private sealed class SequenceSampler(params long[] values) : IWorkingSetSampler
    {
        private int index;
        public TaskCompletionSource SecondSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public long CaptureWorkingSetBytes()
        {
            int current = Interlocked.Increment(ref index) - 1;
            if (current == 1) SecondSample.TrySetResult();
            return values[Math.Min(current, values.Length - 1)];
        }
    }

    private sealed class ThrowingSampler : IWorkingSetSampler
    {
        public long CaptureWorkingSetBytes() => throw new InvalidOperationException("probe failed");
    }
}
