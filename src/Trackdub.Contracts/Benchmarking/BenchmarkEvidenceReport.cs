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
    public int SchemaVersion { get; init; } = 1;
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
    public IReadOnlyDictionary<string, long?> MemoryBytes { get; init; } = new Dictionary<string, long?>();
    public IReadOnlyList<BenchmarkEvidenceStage> Stages { get; init; } = [];
}
