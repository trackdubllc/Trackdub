using System.Diagnostics;

namespace Trackdub.Contracts.Benchmarking;

/// <summary>
/// Optional per-run phase timings. Ordinary pipeline runs pay only a null check.
/// The ambient scope flows with async stage work and never stores content or paths.
/// </summary>
public sealed class BenchmarkPhaseCapture
{
    private static readonly AsyncLocal<BenchmarkPhaseCapture?> Ambient = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, (double Milliseconds, int Count)> _totals = new(StringComparer.Ordinal);

    public static IDisposable Activate(BenchmarkPhaseCapture capture)
    {
        ArgumentNullException.ThrowIfNull(capture);
        BenchmarkPhaseCapture? previous = Ambient.Value;
        Ambient.Value = capture;
        return new Scope(previous);
    }

    public static Timer Start(string phase) =>
        Ambient.Value is { } capture
            ? new Timer(capture, phase, Stopwatch.GetTimestamp())
            : default;

    public IReadOnlyDictionary<string, double?> SnapshotMilliseconds()
    {
        lock (_gate)
        {
            return _totals.ToDictionary(
                pair => $"phase:{pair.Key}",
                pair => (double?)pair.Value.Milliseconds,
                StringComparer.Ordinal);
        }
    }

    private void Record(string phase, long start)
    {
        double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        lock (_gate)
        {
            _totals.TryGetValue(phase, out var previous);
            _totals[phase] = (previous.Milliseconds + elapsed, previous.Count + 1);
        }
    }

    public readonly struct Timer : IDisposable
    {
        private readonly BenchmarkPhaseCapture? _capture;
        private readonly string? _phase;
        private readonly long _start;

        internal Timer(BenchmarkPhaseCapture capture, string phase, long start)
        {
            _capture = capture;
            _phase = phase;
            _start = start;
        }

        public void Dispose()
        {
            if (_capture is not null && _phase is not null)
                _capture.Record(_phase, _start);
        }
    }

    private sealed class Scope(BenchmarkPhaseCapture? previous) : IDisposable
    {
        public void Dispose() => Ambient.Value = previous;
    }
}
