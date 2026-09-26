using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Rolls per-iteration validation checks into per-stage, per-metric distributions.
/// </summary>
/// <remarks>
/// Uses <see cref="PercentileCalculator"/> so telemetry percentiles are computed with the same
/// Type 7 formula as latency percentiles; a reader comparing the two tables is comparing like
/// with like.
/// </remarks>
public static class ResourceTelemetryAggregator
{
    /// <summary>
    /// Groups <paramref name="samples"/> by stage and phase, then summarizes each metric across
    /// the iterations in that group. Returns one entry per (stage, phase, metric) combination
    /// that produced at least one check, ordered deterministically.
    /// </summary>
    public static IReadOnlyList<ResourceTelemetryDistribution> Aggregate(
        IEnumerable<BenchmarkStageResourceTelemetry> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        return samples
            .GroupBy(sample => (sample.Stage, sample.Phase), StringTupleComparer.Instance)
            .OrderBy(group => group.Key.Stage, StringComparer.OrdinalIgnoreCase)
            .ThenBy(group => group.Key.Phase, StringComparer.OrdinalIgnoreCase)
            .Select(group => Summarize(group.Key.Stage, group.Key.Phase, group.ToArray()))
            .ToArray();
    }

    private static ResourceTelemetryDistribution Summarize(
        string stage, string phase, BenchmarkStageResourceTelemetry[] samples)
    {
        var metrics = new List<ResourceMetricStatistics>();
        foreach (IGrouping<string, ResourceTelemetryCheck> metricGroup in samples
            .SelectMany(sample => sample.Validation.Checks)
            .Where(check => check.Metric != "stage")
            .GroupBy(check => check.Metric, StringComparer.Ordinal))
        {
            ResourceTelemetryCheck[] checks = metricGroup.ToArray();
            double[] observed = checks
                .Where(check => check.ObservedValue.HasValue && double.IsFinite(check.ObservedValue.Value))
                .Select(check => check.ObservedValue!.Value)
                .OrderBy(value => value)
                .ToArray();

            double? threshold = checks.FirstOrDefault(check => check.Threshold.HasValue)?.Threshold;
            int failing = threshold.HasValue
                ? checks.Count(check => check.Status == ResourceTelemetryStatus.Failed)
                : 0;

            metrics.Add(new ResourceMetricStatistics
            {
                Metric = metricGroup.Key,
                SampleCount = observed.Length,
                UnavailableSampleCount = checks.Length - observed.Length,
                FailingSampleCount = failing,
                Minimum = observed.Length > 0 ? observed[0] : 0d,
                Maximum = observed.Length > 0 ? observed[^1] : 0d,
                Mean = observed.Length > 0 ? observed.Average() : 0d,
                P50 = PercentileCalculator.CalculatePercentile(observed, 0.50d),
                P95 = PercentileCalculator.CalculatePercentile(observed, 0.95d),
                P99 = PercentileCalculator.CalculatePercentile(observed, 0.99d),
                Threshold = threshold,
                // Any failure dominates, matching the per-iteration validation rules.
                Status = checks.Any(check => check.Status == ResourceTelemetryStatus.Failed)
                    ? ResourceTelemetryStatus.Failed
                    : checks.Any(check => check.Status == ResourceTelemetryStatus.Unavailable)
                        ? ResourceTelemetryStatus.Unavailable
                        : checks.Any(check => check.Status == ResourceTelemetryStatus.Skipped)
                            ? ResourceTelemetryStatus.Skipped
                            : ResourceTelemetryStatus.Passed,
            });
        }

        return new ResourceTelemetryDistribution
        {
            Stage = stage,
            Phase = phase,
            IterationCount = samples.Select(sample => sample.Iteration).Distinct().Count(),
            Metrics = metrics,
        };
    }

    private sealed class StringTupleComparer : IEqualityComparer<(string Stage, string Phase)>
    {
        public static StringTupleComparer Instance { get; } = new();

        public bool Equals((string Stage, string Phase) x, (string Stage, string Phase) y) =>
            StringComparer.OrdinalIgnoreCase.Equals(x.Stage, y.Stage) &&
            StringComparer.OrdinalIgnoreCase.Equals(x.Phase, y.Phase);

        public int GetHashCode((string Stage, string Phase) obj) =>
            HashCode.Combine(
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Stage),
                StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Phase));
    }
}
