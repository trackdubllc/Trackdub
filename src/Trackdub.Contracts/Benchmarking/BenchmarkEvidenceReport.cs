using System.Text.Json.Serialization;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Contracts.Benchmarking;

public enum BenchmarkEvidenceKind { Observation, Benchmark }

public enum BenchmarkEvidenceStatus { Completed, PartiallyCompleted, Skipped, Failed, Canceled }

public sealed record BenchmarkEvidenceStage
{
    public required string Name { get; init; }
    public required BenchmarkEvidenceStatus Status { get; init; }
    public string? Reason { get; init; }
    public Guid? StageRunId { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public DateTimeOffset? CompletedAtUtc { get; init; }
    public double? DurationMilliseconds { get; init; }
    public string? RequestedModel { get; init; }
    public string? ActualModel { get; init; }
    public string? RequestedProvider { get; init; }
    public string? ActualProvider { get; init; }
}

/// <summary>Portable, path-free evidence. Durations are measured with a monotonic clock when available.</summary>
public sealed record BenchmarkEvidenceReport
{
    private BenchmarkProcessMemoryTelemetry? processMemory;
    private IReadOnlyList<BenchmarkStageGarbageCollectionTelemetry>? stageGarbageCollection;

    /// <summary>Schema-v1 memory evidence retained only for compatibility with persisted reports.</summary>
    [JsonPropertyName("MemoryBytes")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public IReadOnlyDictionary<string, long?>? LegacyMemoryBytes { get; init; }

    /// <summary>
    /// Evidence schema version. Bumped to 2 when the legacy string-keyed
    /// <c>MemoryBytes</c> map was replaced by the typed <see cref="ProcessMemory"/> and
    /// <see cref="StageGarbageCollection"/> records.
    /// </summary>
    public int SchemaVersion { get; init; } = 2;
    public required Guid RunId { get; init; }
    public required BenchmarkEvidenceKind Kind { get; init; }
    public required string Scenario { get; init; }
    public required string RunMode { get; init; }
    public required BenchmarkEvidenceStatus Status { get; init; }
    public string? Reason { get; init; }
    public DateTimeOffset? StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public string? FixtureSha256 { get; init; }
    public string? RequestedModel { get; init; }
    public string? ActualModel { get; init; }
    public string? RequestedProvider { get; init; }
    public string? ActualProvider { get; init; }
    public IReadOnlyDictionary<string, string> Configuration { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, string> RuntimeVersions { get; init; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, double?> TimingsMilliseconds { get; init; } = new Dictionary<string, double?>();

    /// <summary>
    /// Run-level process memory envelope: the start/end working-set samples, the
    /// continuously-sampled peak, the whole-run managed allocation total, and the GC collection
    /// deltas. Per-stage working set and managed allocation live in <see cref="ResourceTelemetry"/>;
    /// per-stage GC deltas in <see cref="StageGarbageCollection"/>. This typed record replaces the
    /// legacy string-keyed <c>MemoryBytes</c> map.
    /// </summary>
    public BenchmarkProcessMemoryTelemetry? ProcessMemory
    {
        get => processMemory ?? (LegacyMemoryBytes is null ? null : new()
        {
            WorkingSetStartBytes = LegacyValue("processWorkingSetStart"),
            WorkingSetEndBytes = LegacyValue("processWorkingSetEnd"),
            PeakWorkingSetBytes = LegacyValue("peakWorkingSetBytes") ?? LegacyValue("processPeakWorkingSet"),
            ManagedAllocatedBytes = LegacyValue("managedAllocatedBytes"),
            Gen0Collections = LegacyValue("gen0Collections"),
            Gen1Collections = LegacyValue("gen1Collections"),
            Gen2Collections = LegacyValue("gen2Collections"),
        });
        init => processMemory = value;
    }

    /// <summary>
    /// Per-stage GC collection deltas — the only per-stage memory signal the typed
    /// <see cref="BenchmarkStageResourceTelemetry"/> checks do not already carry.
    /// </summary>
    public IReadOnlyList<BenchmarkStageGarbageCollectionTelemetry> StageGarbageCollection
    {
        get => stageGarbageCollection ?? ReadLegacyStageCollections();
        init => stageGarbageCollection = value;
    }

    private long? LegacyValue(string key) =>
        LegacyMemoryBytes is { } memory && memory.TryGetValue(key, out long? value) ? value : null;

    private IReadOnlyList<BenchmarkStageGarbageCollectionTelemetry> ReadLegacyStageCollections()
    {
        if (LegacyMemoryBytes is null) return [];
        string[] stages = LegacyMemoryBytes.Keys
            .Where(key => key.StartsWith("stage:", StringComparison.Ordinal)
                && (key.EndsWith(":gen0", StringComparison.Ordinal)
                    || key.EndsWith(":gen1", StringComparison.Ordinal)
                    || key.EndsWith(":gen2", StringComparison.Ordinal)))
            .Select(key => key[6..key.LastIndexOf(':')]).Distinct(StringComparer.Ordinal).ToArray();
        return stages.Select(stage => new BenchmarkStageGarbageCollectionTelemetry
        {
            Stage = stage,
            Gen0Collections = LegacyValue($"stage:{stage}:gen0"),
            Gen1Collections = LegacyValue($"stage:{stage}:gen1"),
            Gen2Collections = LegacyValue($"stage:{stage}:gen2"),
        }).ToArray();
    }

    /// <summary>Measured counters (summed across iterations) and maxima (peak across
    /// iterations) recorded by <see cref="BenchmarkPhaseCapture"/> under raw names.</summary>
    public IReadOnlyDictionary<string, long?> Counters { get; init; } = new Dictionary<string, long?>();
    public IReadOnlyList<BenchmarkEvidenceStage> Stages { get; init; } = [];
    public ResourceTelemetryBounds? ResourceTelemetryBounds { get; init; }
    public ResourceTelemetryStatus? ResourceValidationStatus { get; init; }
    public IReadOnlyList<BenchmarkStageResourceTelemetry> ResourceTelemetry { get; init; } = [];

    /// <summary>Per-stage, per-phase distributions over the iterations in <see cref="ResourceTelemetry"/>.</summary>
    public IReadOnlyList<ResourceTelemetryDistribution> ResourceDistribution { get; init; } = [];
}

public sealed record BenchmarkStageResourceTelemetry
{
    public required string Stage { get; init; }
    public required string Phase { get; init; }
    public int Iteration { get; init; }
    public int Attempt { get; init; }
    public required BenchmarkEvidenceStatus ExecutionStatus { get; init; }
    public string? Reason { get; init; }
    public required ResourceTelemetryValidation Validation { get; init; }
}

/// <summary>
/// Process-wide memory envelope for one benchmark run. Endpoint working sets are point-in-time
/// samples; <see cref="PeakWorkingSetBytes"/> comes from continuous interval sampling when it ran,
/// otherwise it is the interval's endpoint maximum. GC values are deltas over the run.
/// </summary>
public sealed record BenchmarkProcessMemoryTelemetry
{
    public long? WorkingSetStartBytes { get; init; }
    public long? WorkingSetEndBytes { get; init; }
    public long? PeakWorkingSetBytes { get; init; }
    public long? ManagedAllocatedBytes { get; init; }
    public long? Gen0Collections { get; init; }
    public long? Gen1Collections { get; init; }
    public long? Gen2Collections { get; init; }
}

/// <summary>
/// Per-stage GC collection deltas for a run, aggregated across that stage's measured iterations.
/// Working-set peaks and managed allocation for the same stage are already carried by the typed
/// per-stage resource checks, so this record deliberately carries only the GC deltas.
/// </summary>
public sealed record BenchmarkStageGarbageCollectionTelemetry
{
    public required string Stage { get; init; }
    public long? Gen0Collections { get; init; }
    public long? Gen1Collections { get; init; }
    public long? Gen2Collections { get; init; }
}
