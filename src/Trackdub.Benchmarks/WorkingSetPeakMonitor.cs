using System.Diagnostics;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Samples process working set during an interval. The peak is sampled at a finite cadence,
/// so excursions shorter than the interval can still be missed.
/// </summary>
internal sealed class WorkingSetPeakMonitor : IWorkingSetPeakMonitor
{
    /// <summary>
    /// A tick gap beyond this multiple of the cadence means several sample windows were
    /// skipped: a single 2x blip is ordinary thread-pool jitter, so only larger dilations warn.
    /// </summary>
    internal const int DilationWarnMultiple = 4;

    private readonly IWorkingSetSampler sampler;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task samplingTask;
    private readonly object stopGate = new();
    private long peakBytes;
    private int hasSample;
    private int stopped;
    private string? unavailableReason;
    private long maxTickGapTicks;

    public WorkingSetPeakMonitor(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval = null)
        : this(sampler, initialValue, interval, ticker: null)
    {
    }

    internal WorkingSetPeakMonitor(
        IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval, ISamplingTicker? ticker)
    {
        this.sampler = sampler ?? throw new ArgumentNullException(nameof(sampler));
        this.interval = interval ?? TimeSpan.FromMilliseconds(25);
        if (this.interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval), "Sampling interval must be positive.");
        }

        if (initialValue is >= 0)
        {
            peakBytes = initialValue.Value;
            hasSample = 1;
        }

        Capture();
        samplingTask = SampleUntilStoppedAsync(ticker ?? new PeriodicSamplingTicker(this.interval));
    }

    public string? UnavailableReason => Volatile.Read(ref unavailableReason);

    /// <summary>
    /// Longest observed gap between consecutive sampling ticks. Contended hosts delay
    /// <see cref="PeriodicTimer"/> callbacks without failing them, so this is how
    /// interval dilation is observed.
    /// </summary>
    public TimeSpan MaxObservedTickInterval => new(Volatile.Read(ref maxTickGapTicks));

    /// <summary>
    /// Advisory warning when the sampling cadence dilated under load. Never affects
    /// the peak result or stage execution; it only records that transient peaks
    /// shorter than the dilated gap may have been missed.
    /// </summary>
    public string? SamplingWarning =>
        DescribeDilationWarning(interval, MaxObservedTickInterval);

    internal static string? DescribeDilationWarning(TimeSpan interval, TimeSpan maxGap) =>
        maxGap > interval * DilationWarnMultiple
            ? $"Working-set sampling cadence dilated (longest tick gap {maxGap.TotalMilliseconds:F0} ms "
                + $"at {interval.TotalMilliseconds:F0} ms cadence); transient peaks shorter than the gap "
                + "may have been missed."
            : null;

    public long? Stop()
    {
        lock (stopGate)
        {
            if (stopped == 0)
            {
                Volatile.Write(ref stopped, 1);
                cancellation.Cancel();
                try
                {
                    samplingTask.GetAwaiter().GetResult();
                }
                catch (OperationCanceledException)
                {
                    // Expected when the stage reaches its terminal boundary.
                }

                Capture(allowStopped: true);
                cancellation.Dispose();
            }

            return Volatile.Read(ref unavailableReason) is null && Volatile.Read(ref hasSample) == 1
                ? Volatile.Read(ref peakBytes)
                : null;
        }
    }

    private async Task SampleUntilStoppedAsync(ISamplingTicker ticker)
    {
        using var _ = ticker;
        long previousTick = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            while (await ticker.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                long tick = ticker.LastTickTimestamp;
                RecordTickGap(System.Diagnostics.Stopwatch.GetElapsedTime(previousTick, tick));
                previousTick = tick;
                Capture();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Terminal boundary ends interval sampling.
        }
    }

    private void RecordTickGap(TimeSpan gap)
    {
        // Single writer (the sampling loop); readers observe via Volatile.
        long ticks = gap.Ticks;
        long observed;
        do
        {
            observed = Volatile.Read(ref maxTickGapTicks);
            if (ticks <= observed) return;
        }
        while (Interlocked.CompareExchange(ref maxTickGapTicks, ticks, observed) != observed);
    }

    private void Capture(bool allowStopped = false)
    {
        try
        {
            if (!allowStopped && Volatile.Read(ref stopped) != 0) return;
            long sample = sampler.CaptureWorkingSetBytes();
            if (sample < 0)
            {
                Volatile.Write(ref unavailableReason, "Working-set sampler returned a negative reading.");
                return;
            }

            Interlocked.Exchange(ref hasSample, 1);
            long observed;
            do
            {
                observed = Volatile.Read(ref peakBytes);
                if (sample <= observed) return;
            }
            while (Interlocked.CompareExchange(ref peakBytes, sample, observed) != observed);
        }
        catch (Exception exception) when (TelemetryExceptionFilters.IsWorkingSetSamplingFailure(exception))
        {
            // Telemetry is best-effort: a plugin or OS failure must never change stage execution.
            Volatile.Write(ref unavailableReason,
                $"Continuous working-set sampling unavailable ({exception.GetType().Name}).");
        }
    }
}
