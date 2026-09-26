using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Samples process working set during an interval. The peak is sampled at a finite cadence,
/// so excursions shorter than the interval can still be missed.
/// </summary>
internal sealed class WorkingSetPeakMonitor
{
    private readonly IWorkingSetSampler sampler;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task samplingTask;
    private long peakBytes;
    private int hasSample;
    private int stopped;
    private string? unavailableReason;

    public WorkingSetPeakMonitor(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval = null)
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
        samplingTask = SampleUntilStoppedAsync();
    }

    public string? UnavailableReason => Volatile.Read(ref unavailableReason);

    public long? Stop()
    {
        if (Interlocked.Exchange(ref stopped, 1) == 0)
        {
            cancellation.Cancel();
            try
            {
                samplingTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
                // Expected when the stage reaches its terminal boundary.
            }

            Capture();
            cancellation.Dispose();
        }

        return Volatile.Read(ref unavailableReason) is null && Volatile.Read(ref hasSample) == 1
            ? Volatile.Read(ref peakBytes)
            : null;
    }

    private async Task SampleUntilStoppedAsync()
    {
        using var timer = new PeriodicTimer(interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellation.Token).ConfigureAwait(false))
            {
                Capture();
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
        {
            // Terminal boundary ends interval sampling.
        }
    }

    private void Capture()
    {
        try
        {
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
        catch (Exception exception) when (exception is System.ComponentModel.Win32Exception or
            NotSupportedException or UnauthorizedAccessException or InvalidOperationException or IOException)
        {
            Volatile.Write(ref unavailableReason,
                $"Continuous working-set sampling unavailable ({exception.GetType().Name}).");
        }
    }
}
