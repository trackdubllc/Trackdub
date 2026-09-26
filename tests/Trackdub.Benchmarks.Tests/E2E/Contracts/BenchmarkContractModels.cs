namespace Trackdub.Benchmarks.Tests.E2E.Contracts;

/// <summary>
/// Opaque-box contract model for latency and throughput statistics per PROJECT.md § Interface Contracts.
/// </summary>
public sealed record LatencyStatistics(
    double MinMilliseconds,
    double MaxMilliseconds,
    double MeanMilliseconds,
    double P50Milliseconds,
    double P90Milliseconds,
    double P99Milliseconds,
    double ThroughputUnitsPerSecond,
    int SampleCount);

/// <summary>
/// Snapshot of process and stage resource telemetry at a specific instant.
/// </summary>
public sealed record ResourceTelemetrySnapshot(
    long WorkingSetBytes,
    long PeakWorkingSetBytes,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// Telemetry delta between two points in time across a benchmark execution.
/// </summary>
public sealed record ResourceTelemetryDelta(
    long WorkingSetDeltaBytes,
    long PeakWorkingSetBytes,
    long ManagedAllocatedBytes,
    int Gen0Collections,
    int Gen1Collections,
    int Gen2Collections);

/// <summary>
/// Comparative metrics for an execution provider against a baseline.
/// </summary>
public sealed record ProviderComparisonMetrics(
    string Provider,
    double P50Milliseconds,
    double SpeedupFactor,
    double LatencyDeltaMilliseconds,
    double ThroughputRatio,
    long PeakWorkingSetDeltaBytes,
    long ManagedAllocatedDeltaBytes);

/// <summary>
/// Multi-provider matrix comparison report.
/// </summary>
public sealed record ExecutionProviderMatrixReport(
    string Scenario,
    string BaselineProvider,
    IReadOnlyList<ProviderComparisonMetrics> Comparisons,
    DateTimeOffset Timestamp);

/// <summary>
/// Quality gate rule definition asserting performance and resource budgets.
/// </summary>
public sealed record QualityGateRule(
    double? MaxP50LatencyMilliseconds = null,
    double? MaxP99LatencyMilliseconds = null,
    double? MinSpeedupFactor = null,
    long? MaxPeakWorkingSetBytes = null,
    long? MaxManagedAllocatedBytes = null);

/// <summary>
/// Evaluation result of a quality gate assessment.
/// </summary>
public sealed record QualityGateResult(
    bool Passed,
    IReadOnlyList<string> Violations);
