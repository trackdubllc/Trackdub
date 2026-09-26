using System.Diagnostics;
using System.Globalization;
using System.Text;
using Trackdub.Benchmarks.Tests.E2E.Contracts;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.E2E.Oracles;

/// <summary>
/// Authoritative mathematical oracle and reference derivation for Trackdub performance benchmarks.
/// Conforms to PROJECT.md interface contracts and PcmAudioQualityAnalyzer percentile formulas.
/// </summary>
public static class BenchmarkCalculationOracle
{
    /// <summary>
    /// Calculates five-point summary, linear interpolation percentiles (P50, P90, P99),
    /// and throughput from a set of latency samples.
    /// </summary>
    public static LatencyStatistics CalculatePercentiles(
        IReadOnlyList<double> samplesMilliseconds,
        double totalUnits = 0,
        double totalDurationSeconds = 0)
    {
        if (samplesMilliseconds == null || samplesMilliseconds.Count == 0)
        {
            return new LatencyStatistics(0, 0, 0, 0, 0, 0, 0, 0);
        }

        double[] sorted = samplesMilliseconds.OrderBy(v => v).ToArray();
        double min = sorted[0];
        double max = sorted[^1];
        double mean = sorted.Average();

        double p50 = InterpolatePercentile(sorted, 0.50);
        double p90 = InterpolatePercentile(sorted, 0.90);
        double p99 = InterpolatePercentile(sorted, 0.99);

        double throughput;
        if (totalDurationSeconds > 0)
        {
            throughput = totalUnits / totalDurationSeconds;
        }
        else if (totalUnits > 0 && mean > 0)
        {
            throughput = totalUnits / (mean / 1000.0);
        }
        else
        {
            throughput = mean > 0 ? 1000.0 / mean : 0;
        }

        return new LatencyStatistics(
            MinMilliseconds: min,
            MaxMilliseconds: max,
            MeanMilliseconds: mean,
            P50Milliseconds: p50,
            P90Milliseconds: p90,
            P99Milliseconds: p99,
            ThroughputUnitsPerSecond: throughput,
            SampleCount: sorted.Length);
    }

    /// <summary>
    /// Computes rank-interpolated percentile matching PcmAudioQualityAnalyzer.cs:460-477.
    /// </summary>
    public static double InterpolatePercentile(IReadOnlyList<double> sortedValues, double percentile)
    {
        if (sortedValues.Count == 0)
        {
            return 0;
        }

        if (sortedValues.Count == 1)
        {
            return sortedValues[0];
        }

        double clampedPercentile = Math.Clamp(percentile, 0.0, 1.0);
        double position = clampedPercentile * (sortedValues.Count - 1);
        int left = (int)Math.Floor(position);
        int right = (int)Math.Ceiling(position);

        if (left == right)
        {
            return sortedValues[left];
        }

        double fraction = position - left;
        return sortedValues[left] + ((sortedValues[right] - sortedValues[left]) * fraction);
    }

    /// <summary>
    /// Captures process-level memory telemetry.
    /// </summary>
    public static ResourceTelemetrySnapshot CaptureProcessTelemetry()
    {
        using var process = Process.GetCurrentProcess();
        // Mirrors ResourceTelemetry.CaptureProcess: some platforms (e.g. macOS) don't report a
        // peak distinct from the current working set, so PeakWorkingSet64 can come back below
        // WorkingSet64. Clamp it so it always reflects at least the current usage.
        long workingSet = process.WorkingSet64;
        long peakWorkingSet = Math.Max(process.PeakWorkingSet64, workingSet);
        return new ResourceTelemetrySnapshot(
            WorkingSetBytes: workingSet,
            PeakWorkingSetBytes: peakWorkingSet,
            ManagedAllocatedBytes: GC.GetTotalAllocatedBytes(precise: false),
            Gen0Collections: GC.CollectionCount(0),
            Gen1Collections: GC.CollectionCount(1),
            Gen2Collections: GC.CollectionCount(2));
    }

    /// <summary>
    /// Calculates memory telemetry delta between two snapshots.
    /// </summary>
    public static ResourceTelemetryDelta CalculateTelemetryDelta(
        ResourceTelemetrySnapshot start,
        ResourceTelemetrySnapshot end)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(end);

        return new ResourceTelemetryDelta(
            WorkingSetDeltaBytes: end.WorkingSetBytes - start.WorkingSetBytes,
            // Mirrors ResourceTelemetry.CalculateDelta: an endpoint-sampled max of the two
            // snapshots' WorkingSetBytes, not the snapshots' own process-lifetime peak field.
            PeakWorkingSetBytes: Math.Max(start.WorkingSetBytes, end.WorkingSetBytes),
            ManagedAllocatedBytes: Math.Max(0, end.ManagedAllocatedBytes - start.ManagedAllocatedBytes),
            Gen0Collections: Math.Max(0, end.Gen0Collections - start.Gen0Collections),
            Gen1Collections: Math.Max(0, end.Gen1Collections - start.Gen1Collections),
            Gen2Collections: Math.Max(0, end.Gen2Collections - start.Gen2Collections));
    }

    /// <summary>
    /// Computes comparative metrics across execution providers.
    /// </summary>
    public static ExecutionProviderMatrixReport CompareProviders(
        string scenario,
        string baselineProvider,
        IReadOnlyDictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)> providerStats,
        DateTimeOffset? timestamp = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(scenario);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineProvider);
        ArgumentNullException.ThrowIfNull(providerStats);

        if (!providerStats.TryGetValue(baselineProvider, out var baseline))
        {
            throw new ArgumentException($"Baseline provider '{baselineProvider}' was not found in provider stats.", nameof(baselineProvider));
        }

        var comparisons = new List<ProviderComparisonMetrics>(providerStats.Count);
        foreach ((string provider, var stats) in providerStats)
        {
            double speedup = stats.P50 > 0 ? baseline.P50 / stats.P50 : 1.0;
            double latencyDelta = stats.P50 - baseline.P50;
            double throughputRatio = baseline.Throughput > 0 ? stats.Throughput / baseline.Throughput : 1.0;
            long peakMemoryDelta = stats.PeakMemory - baseline.PeakMemory;
            long managedAllocDelta = stats.ManagedAlloc - baseline.ManagedAlloc;

            comparisons.Add(new ProviderComparisonMetrics(
                Provider: provider,
                P50Milliseconds: stats.P50,
                SpeedupFactor: speedup,
                LatencyDeltaMilliseconds: latencyDelta,
                ThroughputRatio: throughputRatio,
                PeakWorkingSetDeltaBytes: peakMemoryDelta,
                ManagedAllocatedDeltaBytes: managedAllocDelta));
        }

        return new ExecutionProviderMatrixReport(
            Scenario: scenario,
            BaselineProvider: baselineProvider,
            Comparisons: comparisons,
            Timestamp: timestamp ?? DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Formats an ExecutionProviderMatrixReport into a GitHub-compatible Markdown summary table.
    /// </summary>
    public static string FormatMarkdownSummary(ExecutionProviderMatrixReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine(CultureInfo.InvariantCulture, $"# Benchmark Matrix Summary: {report.Scenario}");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Baseline Provider**: `{report.BaselineProvider}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Timestamp**: `{report.Timestamp:yyyy-MM-dd HH:mm:ss} UTC`");
        sb.AppendLine();
        sb.AppendLine("| Provider | P50 Latency (ms) | Speedup | Latency Delta (ms) | Throughput Ratio | Peak WS Delta (MB) | Managed Alloc Delta (MB) |");
        sb.AppendLine("|:---|---:|---:|---:|---:|---:|---:|");

        foreach (ProviderComparisonMetrics c in report.Comparisons)
        {
            string speedupBadge = c.SpeedupFactor >= 1.05
                ? $"{c.SpeedupFactor:F2}x :rocket:"
                : c.SpeedupFactor <= 0.95
                    ? $"{c.SpeedupFactor:F2}x :warning:"
                    : $"{c.SpeedupFactor:F2}x";

            double peakMb = c.PeakWorkingSetDeltaBytes / (1024.0 * 1024.0);
            double allocMb = c.ManagedAllocatedDeltaBytes / (1024.0 * 1024.0);

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{c.Provider}` | {c.P50Milliseconds:F2} | {speedupBadge} | {c.LatencyDeltaMilliseconds:+0.00;-0.00;0.00} | {c.ThroughputRatio:F2}x | {peakMb:+0.00;-0.00;0.00} | {allocMb:+0.00;-0.00;0.00} |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Formats a ControlledStageBenchmarkMatrixReport into a GitHub-compatible Markdown summary table.
    /// </summary>
    public static string FormatMarkdownSummary(ControlledStageBenchmarkMatrixReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        var sb = new StringBuilder();
        sb.AppendLine("# Controlled Stage Matrix Summary");
        sb.AppendLine();
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Fixture**: `{report.FixturePath}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Status**: `{report.Status}`");
        sb.AppendLine(CultureInfo.InvariantCulture, $"- **Duration**: `{(report.CompletedAtUtc - report.StartedAtUtc).TotalSeconds:F2}s`");
        sb.AppendLine();
        sb.AppendLine("| Stage | Status | Duration (ms) | Provider | Model |");
        sb.AppendLine("|:---|:---|---:|:---|:---|");

        foreach (ControlledStageBenchmarkMatrixResult result in report.Results)
        {
            BenchmarkEvidenceReport evidence = result.Evidence;
            double duration = evidence.TimingsMilliseconds.TryGetValue("pipeline", out double? val) && val.HasValue
                ? val.Value
                : 0;

            sb.AppendLine(CultureInfo.InvariantCulture,
                $"| `{result.Stage}` | `{evidence.Status}` | {duration:F2} | `{evidence.ActualProvider ?? "N/A"}` | `{evidence.ActualModel ?? "N/A"}` |");
        }

        return sb.ToString();
    }

    /// <summary>
    /// Evaluates a quality gate against benchmark matrix results.
    /// </summary>
    public static QualityGateResult EvaluateQualityGate(ExecutionProviderMatrixReport report, QualityGateRule rule)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(rule);

        var violations = new List<string>();

        foreach (ProviderComparisonMetrics c in report.Comparisons)
        {
            if (c.Provider == report.BaselineProvider)
            {
                continue;
            }

            if (rule.MaxP50LatencyMilliseconds.HasValue && c.P50Milliseconds > rule.MaxP50LatencyMilliseconds.Value)
            {
                violations.Add($"Provider '{c.Provider}' P50 latency {c.P50Milliseconds:F2}ms exceeds threshold {rule.MaxP50LatencyMilliseconds.Value:F2}ms.");
            }

            if (rule.MinSpeedupFactor.HasValue && c.SpeedupFactor < rule.MinSpeedupFactor.Value)
            {
                violations.Add($"Provider '{c.Provider}' speedup {c.SpeedupFactor:F2}x is below required {rule.MinSpeedupFactor.Value:F2}x.");
            }
        }

        return new QualityGateResult(violations.Count == 0, violations);
    }
}
