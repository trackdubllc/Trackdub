namespace Trackdub.Benchmarks.Scenarios;

/// <summary>
/// Comparative metrics for an execution provider against a specified baseline provider.
/// </summary>
/// <param name="Provider">The execution provider name (e.g. "cpu", "directml", "tensorrt").</param>
/// <param name="P50Milliseconds">The median (P50) execution latency in milliseconds.</param>
/// <param name="SpeedupFactor">The relative speedup factor compared to the baseline (<c>Baseline.P50 / Provider.P50</c>).</param>
/// <param name="LatencyDeltaMilliseconds">The signed latency delta compared to the baseline (<c>Provider.P50 - Baseline.P50</c>). Negative values indicate latency reduction.</param>
/// <param name="ThroughputRatio">The relative throughput ratio compared to the baseline (<c>Provider.Throughput / Baseline.Throughput</c>).</param>
/// <param name="PeakWorkingSetDeltaBytes">The signed peak working set delta in bytes (<c>Provider.PeakWorkingSet - Baseline.PeakWorkingSet</c>).</param>
/// <param name="ManagedAllocatedDeltaBytes">The signed managed heap allocation delta in bytes (<c>Provider.ManagedAllocated - Baseline.ManagedAllocated</c>).</param>
public sealed record ProviderComparisonMetrics(
    string Provider,
    double P50Milliseconds,
    double SpeedupFactor,
    double LatencyDeltaMilliseconds,
    double ThroughputRatio,
    long PeakWorkingSetDeltaBytes,
    long ManagedAllocatedDeltaBytes);

/// <summary>
/// Cross-execution provider benchmark comparison matrix report.
/// </summary>
/// <param name="Scenario">The scenario identifier (e.g. "full-pipeline", "asr-model").</param>
/// <param name="BaselineProvider">The baseline provider used as the benchmark reference (typically "cpu").</param>
/// <param name="Comparisons">The collection of comparison metrics for each evaluated execution provider.</param>
/// <param name="Timestamp">The UTC timestamp when the matrix comparison was generated.</param>
public sealed record ExecutionProviderMatrixReport(
    string Scenario,
    string BaselineProvider,
    IReadOnlyList<ProviderComparisonMetrics> Comparisons,
    DateTimeOffset Timestamp);
