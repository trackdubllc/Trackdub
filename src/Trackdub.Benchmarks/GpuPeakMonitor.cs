using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Samples this process's dedicated GPU memory during an interval. The peak is sampled at a
/// finite cadence, so excursions shorter than the interval can still be missed. Null readings
/// (a platform or driver that cannot report) are gaps, not zeros: a stage that never yields a
/// sample reports no peak.
/// </summary>
internal sealed class GpuPeakMonitor
{
    private readonly IProcessGpuMemoryReader reader;
    private readonly TimeSpan interval;
    private readonly CancellationTokenSource cancellation = new();
    private readonly Task samplingTask;
    private readonly object stopGate = new();
    private long peakBytes;
    private int hasSample;
    private int stopped;
    private string? unavailableReason;

    public GpuPeakMonitor(IProcessGpuMemoryReader reader, long? initialValue, TimeSpan? interval = null)
    {
        this.reader = reader ?? throw new ArgumentNullException(nameof(reader));
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

    private void Capture(bool allowStopped = false)
    {
        try
        {
            if (!allowStopped && Volatile.Read(ref stopped) != 0) return;
            long? sample = reader.ReadDedicatedGpuMemoryBytes();
            if (sample is null)
            {
                return;
            }

            if (sample < 0)
            {
                Volatile.Write(ref unavailableReason, "Process GPU memory reader returned a negative reading.");
                return;
            }

            Interlocked.Exchange(ref hasSample, 1);
            long observed;
            do
            {
                observed = Volatile.Read(ref peakBytes);
                if (sample.Value <= observed) return;
            }
            while (Interlocked.CompareExchange(ref peakBytes, sample.Value, observed) != observed);
        }
        catch (Exception exception) when (
            exception is ObjectDisposedException or
                         InvalidOperationException or
                         NotSupportedException or
                         System.ComponentModel.Win32Exception or
                         UnauthorizedAccessException or
                         System.Security.SecurityException)
        {
            // Telemetry is best-effort: a plugin or OS failure must never change stage execution.
            Volatile.Write(ref unavailableReason,
                $"Continuous process-GPU sampling unavailable ({exception.GetType().Name}).");
        }
    }
}
