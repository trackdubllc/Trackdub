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
    public async Task Sampling_warns_on_a_scripted_dilated_gap_without_disturbing_the_peakAsync()
    {
        // One-sided by construction: the scripted 150 ms gap can only grow under host
        // load, never shrink below the 100 ms (4x25 ms) threshold, so this cannot flake.
        var monitor = new WorkingSetPeakMonitor(
            new SequenceSampler(100, 100, 100),
            100,
            TimeSpan.FromMilliseconds(25),
            new ScriptedTicker([TimeSpan.FromMilliseconds(10), TimeSpan.FromMilliseconds(150)]));

        // Poll the asserted condition itself: the scripted gap lands no earlier than
        // 150 ms in, and Stop() cancels any tick still in flight, so this cannot overshoot.
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (monitor.MaxObservedTickInterval < TimeSpan.FromMilliseconds(150))
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
        Assert.Equal(100, monitor.Stop());

        Assert.Contains("cadence", monitor.SamplingWarning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Sampling_stays_silent_when_scripted_ticks_hold_cadenceAsync()
    {
        // A one-hour cadence puts the 4x threshold a universe away from millisecond
        // scripted gaps: no host stall can cross it, so the null path through the real
        // wiring is deterministic. (A tight cadence here would reintroduce the very
        // load-sensitivity this machinery warns about.)
        var monitor = new WorkingSetPeakMonitor(
            new SequenceSampler(100, 100, 100, 100),
            100,
            TimeSpan.FromHours(1),
            new ScriptedTicker([
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(5),
                TimeSpan.FromMilliseconds(5),
            ]));

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (monitor.MaxObservedTickInterval == TimeSpan.Zero)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(5), timeout.Token);
        }
        Assert.Equal(100, monitor.Stop());

        Assert.Null(monitor.SamplingWarning);
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

    private sealed class ScriptedTicker(IEnumerable<TimeSpan> script) : ISamplingTicker
    {
        private readonly Queue<TimeSpan> delays = new(script);

        public async ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken)
        {
            if (delays.Count == 0)
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken).ConfigureAwait(false);
                return false;
            }

            await Task.Delay(delays.Dequeue(), cancellationToken).ConfigureAwait(false);
            return true;
        }

        public void Dispose()
        {
        }
    }
}
