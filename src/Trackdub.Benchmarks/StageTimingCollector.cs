using System.Diagnostics;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Benchmarks;

/// <summary>
/// Collects stage start and completion timings and memory/GC telemetry from pipeline progress events.
/// Thread-safe and retains historical samples across multi-run iterations.
/// </summary>
public sealed class StageTimingCollector : IProgress<PipelineProgressEvent>
{
    private readonly object _lock = new();
    private readonly long _runStart;
    private readonly Func<ResourceTelemetrySnapshot?> _snapshotProvider;

    private readonly Dictionary<string, long> _starts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, double> _completions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<double>> _samples = new(StringComparer.OrdinalIgnoreCase);

    private readonly Dictionary<PipelineOutputKind, double> _firstOutputMilliseconds = new();
    private readonly Dictionary<string, ResourceTelemetrySnapshot> _memoryStarts = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResourceTelemetryDelta> _memoryDeltas = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<ResourceTelemetryDelta>> _memorySamples = new(StringComparer.OrdinalIgnoreCase);

    public StageTimingCollector(long runStart)
        : this(runStart, null)
    {
    }

    public StageTimingCollector(
        long runStart,
        Func<ResourceTelemetrySnapshot?>? snapshotProvider)
    {
        _runStart = runStart;
        _snapshotProvider = snapshotProvider ?? ResourceTelemetry.TryCaptureProcess;
    }

    public void Report(PipelineProgressEvent value)
    {
        ArgumentNullException.ThrowIfNull(value);

        lock (_lock)
        {
            if (value.EventKind == PipelineProgressEventKind.Progress &&
                value.OutputKind is { } outputKind &&
                IsTruthfulOutput(value) &&
                !_firstOutputMilliseconds.ContainsKey(outputKind))
            {
                _firstOutputMilliseconds[outputKind] =
                    Stopwatch.GetElapsedTime(_runStart).TotalMilliseconds;
            }

            if (value.EventKind == PipelineProgressEventKind.Started)
            {
                long timestamp = Stopwatch.GetTimestamp();
                ResourceTelemetrySnapshot? memorySnapshot = _snapshotProvider();

                _starts[value.StageKey] = timestamp;
                _memoryStarts.Remove(value.StageKey);
                if (memorySnapshot is not null) _memoryStarts[value.StageKey] = memorySnapshot;

                if (!string.IsNullOrWhiteSpace(value.StageName) &&
                    !string.Equals(value.StageName, value.StageKey, StringComparison.OrdinalIgnoreCase))
                {
                    _starts[value.StageName] = timestamp;
                    _memoryStarts.Remove(value.StageName);
                    if (memorySnapshot is not null) _memoryStarts[value.StageName] = memorySnapshot;
                }
            }
            else if (value.EventKind is PipelineProgressEventKind.Completed or
                     PipelineProgressEventKind.Failed or PipelineProgressEventKind.Skipped)
            {
                bool hasTimingStart = _starts.TryGetValue(value.StageKey, out long start) ||
                    (!string.IsNullOrWhiteSpace(value.StageName) && _starts.TryGetValue(value.StageName, out start));

                bool hasMemoryStart = _memoryStarts.TryGetValue(value.StageKey, out ResourceTelemetrySnapshot? startMemory) ||
                    (!string.IsNullOrWhiteSpace(value.StageName) && _memoryStarts.TryGetValue(value.StageName, out startMemory));

                if (hasTimingStart)
                {
                    double duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                    double completion = Stopwatch.GetElapsedTime(_runStart).TotalMilliseconds;

                    RecordTiming(value.StageKey, duration, completion);
                    if (!string.IsNullOrWhiteSpace(value.StageName) &&
                        !string.Equals(value.StageName, value.StageKey, StringComparison.OrdinalIgnoreCase))
                    {
                        RecordTiming(value.StageName, duration, completion);
                    }
                }

                if (hasMemoryStart && startMemory is not null && _snapshotProvider() is { } endMemory)
                {
                    ResourceTelemetryDelta delta = ResourceTelemetry.CalculateDelta(startMemory, endMemory);

                    RecordMemory(value.StageKey, delta);
                    if (!string.IsNullOrWhiteSpace(value.StageName) &&
                        !string.Equals(value.StageName, value.StageKey, StringComparison.OrdinalIgnoreCase))
                    {
                        RecordMemory(value.StageName, delta);
                    }
                }
            }
        }
    }

    private void RecordTiming(string key, double duration, double completion)
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

    private void RecordMemory(string key, ResourceTelemetryDelta delta)
    {
        _memoryDeltas[key] = delta;
        if (!_memorySamples.TryGetValue(key, out var list))
        {
            list = [];
            _memorySamples[key] = list;
        }

        list.Add(delta);
    }

    public double? GetMilliseconds(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;

        lock (_lock)
        {
            return _durations.TryGetValue(stage, out double ms) ? ms : null;
        }
    }

    /// <summary>
    /// An output event only counts for TTFT when it names a real item index and carries
    /// the persisted identities its kind requires — a malformed event is not evidence.
    /// </summary>
    private static bool IsTruthfulOutput(PipelineProgressEvent value) =>
        value.ItemIndex is >= 0 && value.OutputKind switch
        {
            PipelineOutputKind.TranscriptSegmentAvailable => true,
            PipelineOutputKind.TranscriptSegmentPersisted =>
                value.RevisionId is Guid revisionId && revisionId != Guid.Empty &&
                value.SegmentId is Guid segmentId && segmentId != Guid.Empty,
            PipelineOutputKind.PlayableAudioPersisted =>
                value.SegmentId is Guid segmentId && segmentId != Guid.Empty &&
                value.ArtifactId is Guid artifactId && artifactId != Guid.Empty,
            _ => false,
        };

    public double? GetCompletionMilliseconds(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;

        lock (_lock)
        {
            return _completions.TryGetValue(stage, out double ms) ? ms : null;
        }
    }

    /// <summary>
    /// Elapsed milliseconds from the run start at which the first structured output event
    /// of <paramref name="outputKind"/> arrived, or null when none was observed. First writer
    /// wins; terminal stage-completion events never substitute for a missing output event.
    /// </summary>
    public double? GetFirstOutputMilliseconds(PipelineOutputKind outputKind)
    {
        lock (_lock)
        {
            return _firstOutputMilliseconds.TryGetValue(outputKind, out double ms) ? ms : null;
        }
    }

    public IReadOnlyList<double> GetSamples(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return [];

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

    public ResourceTelemetryDelta? GetMemoryDelta(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;

        lock (_lock)
        {
            return _memoryDeltas.TryGetValue(stage, out var delta) ? delta : null;
        }
    }

    public IReadOnlyDictionary<string, ResourceTelemetryDelta> GetAllMemoryDeltas()
    {
        lock (_lock)
        {
            return new Dictionary<string, ResourceTelemetryDelta>(_memoryDeltas, StringComparer.OrdinalIgnoreCase);
        }
    }

    public IReadOnlyList<ResourceTelemetryDelta> GetMemorySamples(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return [];

        lock (_lock)
        {
            return _memorySamples.TryGetValue(stage, out var list) ? list.ToArray() : [];
        }
    }

    public IReadOnlyDictionary<string, IReadOnlyList<ResourceTelemetryDelta>> GetAllMemorySamples()
    {
        lock (_lock)
        {
            return _memorySamples.ToDictionary(
                kvp => kvp.Key,
                kvp => (IReadOnlyList<ResourceTelemetryDelta>)kvp.Value.ToArray(),
                StringComparer.OrdinalIgnoreCase);
        }
    }

    public ResourceTelemetrySnapshot? GetStartMemorySnapshot(string stage)
    {
        if (string.IsNullOrWhiteSpace(stage)) return null;

        lock (_lock)
        {
            return _memoryStarts.TryGetValue(stage, out var snapshot) ? snapshot : null;
        }
    }
}
