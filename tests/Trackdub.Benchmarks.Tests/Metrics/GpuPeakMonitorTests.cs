using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

/// <summary>
/// Interval GPU peak sampling: the reported peak is the maximum sampled dedicated footprint,
/// gaps are not zeros, and a stage that never yields a sample reports no peak.
/// </summary>
public sealed class GpuPeakMonitorTests
{
    [Fact]
    public async Task Stop_returns_peak_from_intermediate_or_terminal_samples()
    {
        var reader = new SequenceGpuReader(1000, 9000, 3000);
        var monitor = new GpuPeakMonitor(reader, initialValue: 500, TimeSpan.FromMilliseconds(5));

        await reader.SecondSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long? peak = monitor.Stop();

        Assert.Equal(9000, peak);
        Assert.Null(monitor.UnavailableReason);
    }

    [Fact]
    public async Task Gaps_are_not_zeros_and_do_not_clear_the_peak()
    {
        var reader = new SequenceGpuReader(4000, null, null, 2000);
        var monitor = new GpuPeakMonitor(reader, initialValue: null, TimeSpan.FromMilliseconds(5));

        await reader.SecondSample.Task.WaitAsync(TimeSpan.FromSeconds(5));
        long? peak = monitor.Stop();

        Assert.Equal(4000, peak);
        Assert.Null(monitor.UnavailableReason);
    }

    [Fact]
    public void Stop_returns_null_when_no_sample_was_ever_available()
    {
        var monitor = new GpuPeakMonitor(new UnavailableGpuReader(), initialValue: null, TimeSpan.FromSeconds(1));

        Assert.Null(monitor.Stop());
    }

    [Fact]
    public void Stop_returns_unavailable_if_a_read_is_negative()
    {
        var reader = new SequenceGpuReader(1000, -5);
        var monitor = new GpuPeakMonitor(reader, initialValue: null, TimeSpan.FromMilliseconds(5));

        // The negative reading poisons the monitor synchronously in the constructor capture.
        Assert.Null(monitor.Stop());
        Assert.NotNull(monitor.UnavailableReason);
    }

    [Fact]
    public void Constructor_rejects_nonpositive_sampling_interval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new GpuPeakMonitor(new SequenceGpuReader(1), 1, TimeSpan.Zero));
    }

    private sealed class SequenceGpuReader(params long?[] values) : IProcessGpuMemoryReader
    {
        private int index;
        public TaskCompletionSource SecondSample { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public string UnavailableReason => "Test double: script exhausted.";

        public long? ReadDedicatedGpuMemoryBytes()
        {
            int current = Interlocked.Increment(ref index) - 1;
            if (current == 1) SecondSample.TrySetResult();
            return values[Math.Min(current, values.Length - 1)];
        }
    }

    private sealed class UnavailableGpuReader : IProcessGpuMemoryReader
    {
        public string UnavailableReason => "Test double: no reading configured.";

        public long? ReadDedicatedGpuMemoryBytes() => null;
    }
}
