namespace Trackdub.Domain.Benchmarking;

/// <summary>
/// Distribution of one metric's observed values across the iterations of a single run phase.
/// </summary>
/// <remarks>
/// A per-iteration check only answers "did this sample breach?". These statistics answer the
/// question a reader actually has: is the budget usually met, and how close does it run?
/// <see cref="FailingSampleCount"/> makes outliers visible without letting a single bad
/// iteration masquerade as the typical case.
/// </remarks>
public sealed record ResourceMetricStatistics
{
    /// <summary>Metric name, matching the corresponding check's metric.</summary>
    public required string Metric { get; init; }

    /// <summary>Iterations that produced a finite observed value.</summary>
    public required int SampleCount { get; init; }

    /// <summary>Iterations whose observed value was unavailable or skipped.</summary>
    public required int UnavailableSampleCount { get; init; }

    /// <summary>Iterations that breached <see cref="Threshold"/>.</summary>
    public required int FailingSampleCount { get; init; }

    public required double Minimum { get; init; }
    public required double Maximum { get; init; }
    public required double Mean { get; init; }
    public required double P50 { get; init; }
    public required double P95 { get; init; }
    public required double P99 { get; init; }

    /// <summary>Configured bound in the metric's own direction, or null when unbounded.</summary>
    public double? Threshold { get; init; }

    /// <summary>Worst verdict across the iterations: any failure, else any unavailable, else passed.</summary>
    public required ResourceTelemetryStatus Status { get; init; }
}
