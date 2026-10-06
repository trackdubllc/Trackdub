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

    [Theory]
    [InlineData(25, 30, false)]
    [InlineData(25, 100, false)]
    [InlineData(25, 101, true)]
    [InlineData(5, 25, true)]
    public void Dilation_warning_fires_only_beyond_four_times_cadence(
        int intervalMs, int gapMs, bool expectedWarning)
    {
        string? warning = WorkingSetPeakMonitor.DescribeDilationWarning(
            TimeSpan.FromMilliseconds(intervalMs), TimeSpan.FromMilliseconds(gapMs));

        Assert.Equal(expectedWarning, warning is not null);
        if (expectedWarning)
        {
            Assert.Contains("cadence", warning, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("may have been missed", warning, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Sampling_measures_tick_gaps_and_reports_warning_consistentlyAsync()
    {
        var sampler = new SequenceSampler(100, 100, 100, 100);
        var monitor = new WorkingSetPeakMonitor(sampler, 100, TimeSpan.FromMilliseconds(5));

        // The sampler signals its second call, which runs after the first tick gap
        // is already recorded, so this holds regardless of host load.
        await sampler.SecondSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(100, monitor.Stop());

        Assert.True(monitor.MaxObservedTickInterval > TimeSpan.Zero);
        Assert.Equal(
            WorkingSetPeakMonitor.DescribeDilationWarning(
                TimeSpan.FromMilliseconds(5), monitor.MaxObservedTickInterval),
            monitor.SamplingWarning);
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
