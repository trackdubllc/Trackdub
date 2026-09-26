namespace Trackdub.Domain.Benchmarking;

/// <summary>
/// Per-metric distribution for one stage within one run phase, aggregated across iterations.
/// </summary>
public sealed record ResourceTelemetryDistribution
{
    public required string Stage { get; init; }

    /// <summary>Run phase the samples came from (for example "measured" or "warmup").</summary>
    public required string Phase { get; init; }

    public required int IterationCount { get; init; }

    public IReadOnlyList<ResourceMetricStatistics> Metrics { get; init; } = [];
}
