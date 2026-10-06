namespace Trackdub.Benchmarks;

/// <summary>
/// Tick source for continuous working-set sampling. Production ticks come from a
/// <see cref="PeriodicTimer"/>; tests inject scripted gaps to prove dilation detection
/// deterministically (a scripted gap can only grow under host load, never shrink,
/// so threshold crossings are one-sided and cannot flake).
/// </summary>
internal interface ISamplingTicker : IDisposable
{
    ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Timestamp of the last tick returned by <see cref="WaitForNextTickAsync"/>.
    /// The monitor uses this instead of <see cref="System.Diagnostics.Stopwatch"/> so
    /// scripted tick gaps are measured as scripted, not as wall-clock.
    /// </summary>
    long LastTickTimestamp { get; }
}

internal sealed class PeriodicSamplingTicker : ISamplingTicker
{
    private readonly PeriodicTimer timer;

    public PeriodicSamplingTicker(TimeSpan interval) => timer = new PeriodicTimer(interval);

    public ValueTask<bool> WaitForNextTickAsync(CancellationToken cancellationToken) =>
        timer.WaitForNextTickAsync(cancellationToken);

    public long LastTickTimestamp => System.Diagnostics.Stopwatch.GetTimestamp();

    public void Dispose() => timer.Dispose();
}
