using System.Diagnostics;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Benchmarks;

/// <summary>
/// Collects stage start and completion timings from pipeline progress events.
/// Thread-safe and retains historical samples across multi-run iterations.
/// </summary>
public sealed class StageTimingCollector : IProgress<PipelineProgressEvent>
{
    private readonly object _lock = new();
    private readonly long _runStart;
    private readonly Dictionary<string, long> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _completions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);

    public StageTimingCollector(long runStart)
    {
        _runStart = runStart;
    }

    public void Report(PipelineProgressEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            if (value.EventKind == PipelineProgressEventKind.Started)
            {
                long timestamp = Stopwatch.GetTimestamp();
                _starts[value.StageKey] = timestamp;
                if (!string.IsNullOrWhiteSpace(value.StageName) &&
                    !string.Equals(value.StageName, value.StageKey, StringComparison.OrdinalIgnoreCase))
                {
                    _starts[value.StageName] = timestamp;
                }
            }
            else if ((value.EventKind is PipelineProgressEventKind.Completed or
                      PipelineProgressEventKind.Failed or PipelineProgressEventKind.Skipped) &&
                     (_starts.TryGetValue(value.StageKey, out long start) ||
                      (!string.IsNullOrWhiteSpace(value.StageName) && _starts.TryGetValue(value.StageName, out start))))
            {
                double duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                double completion = Stopwatch.GetElapsedTime(_runStart).TotalMilliseconds;

                Record(value.StageKey, duration, completion);
                if (!string.IsNullOrWhiteSpace(value.StageName) &&
                    !string.Equals(value.StageName, value.StageKey, StringComparison.OrdinalIgnoreCase))
                {
                    Record(value.StageName, duration, completion);
                }
            }
        }
    }

    private void Record(string key, double duration, double completion)
    {
        _durations[key] = duration;
        _completions[key] = completion;
        if (!_samples.TryGetValue(key, out var list))
        {
            list = [];
            _samples[key] = list;
        }

        list.Add(duration);
    }

    public double? GetMilliseconds(string stage)
    {
        lock (_lock)
        {
            return _durations.TryGetValue(stage, out double ms) ? ms : null;
        }
    }

    public double? GetCompletionMilliseconds(string stage)
    {
        lock (_lock)
        {
            return _completions.TryGetValue(stage, out double ms) ? ms : null;
        }
    }

    public IReadOnlyList<double> GetSamples(string stage)
    {
        lock (_lock)
        {
            return _samples.TryGetValue(stage, out var list) ? list.ToArray() : [];
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<double>> GetAllSamples()
    {
        lock (_lock)
        {
            return _samples.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<double>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }
}
