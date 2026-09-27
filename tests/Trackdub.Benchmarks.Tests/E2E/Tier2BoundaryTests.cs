using System.Text.Json;
using Trackdub.Benchmarks.Tests.E2E.Contracts;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Benchmarks.Tests.E2E.Oracles;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.E2E;

/// <summary>
/// Tier 2: Boundary, corner cases, and stress tests for requirements R1, R2, R3, R4 (>=5 per requirement).
/// </summary>
public sealed class Tier2BoundaryTests
{
    // =========================================================================
    // Requirement 1 Boundaries: Latency & Percentiles (5 tests)
    // =========================================================================

    [Fact]
    public void R1_T2_01_EmptySamples_ReturnsZeroValuesWithoutThrowing()
    {
        double[] empty = [];

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(empty);

        Assert.Equal(0, stats.SampleCount);
        Assert.Equal(0.0, stats.MinMilliseconds);
        Assert.Equal(0.0, stats.MaxMilliseconds);
        Assert.Equal(0.0, stats.MeanMilliseconds);
        Assert.Equal(0.0, stats.P50Milliseconds);
        Assert.Equal(0.0, stats.P90Milliseconds);
        Assert.Equal(0.0, stats.P99Milliseconds);
        Assert.Equal(0.0, stats.ThroughputUnitsPerSecond);
    }

    [Fact]
    public void R1_T2_02_SingleSample_AllPercentilesMatchSingleValue()
    {
        double[] single = [42.125];

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(single);

        Assert.Equal(1, stats.SampleCount);
        Assert.Equal(42.125, stats.MinMilliseconds);
        Assert.Equal(42.125, stats.MaxMilliseconds);
        Assert.Equal(42.125, stats.MeanMilliseconds);
        Assert.Equal(42.125, stats.P50Milliseconds);
        Assert.Equal(42.125, stats.P90Milliseconds);
        Assert.Equal(42.125, stats.P99Milliseconds);
    }

    [Fact]
    public void R1_T2_03_IdenticalSamples_MinMaxMeanPercentilesAllMatch()
    {
        double[] identical = [77.0, 77.0, 77.0, 77.0, 77.0, 77.0, 77.0];

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(identical);

        Assert.Equal(7, stats.SampleCount);
        Assert.Equal(77.0, stats.MinMilliseconds);
        Assert.Equal(77.0, stats.MaxMilliseconds);
        Assert.Equal(77.0, stats.MeanMilliseconds);
        Assert.Equal(77.0, stats.P50Milliseconds);
        Assert.Equal(77.0, stats.P90Milliseconds);
        Assert.Equal(77.0, stats.P99Milliseconds);
    }

    [Fact]
    public void R1_T2_04_ExtremeOutliers_PercentilesAreResilient()
    {
        // 99 normal samples at 10.0ms and 1 catastrophic outlier at 10,000ms
        double[] samples = Enumerable.Repeat(10.0, 99).Append(10_000.0).ToArray();

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(samples);

        Assert.Equal(10.0, stats.MinMilliseconds);
        Assert.Equal(10_000.0, stats.MaxMilliseconds);
        Assert.True(stats.MeanMilliseconds > 100.0, "Mean should be dragged up by the extreme outlier.");
        Assert.Equal(10.0, stats.P50Milliseconds); // Median is completely resilient
        Assert.Equal(10.0, stats.P90Milliseconds); // P90 is resilient to 1% outlier
        Assert.True(stats.P99Milliseconds > 10.0, "P99 captures the 99th percentile spike.");
    }

    [Fact]
    public void R1_T2_05_ZeroOrNegativeDuration_HandlesThroughputSafely()
    {
        double[] samples = [50.0, 60.0];

        // Total duration is 0
        LatencyStatistics zeroDurationStats = BenchmarkCalculationOracle.CalculatePercentiles(
            samples, totalUnits: 10, totalDurationSeconds: 0);

        Assert.False(double.IsInfinity(zeroDurationStats.ThroughputUnitsPerSecond));
        Assert.False(double.IsNaN(zeroDurationStats.ThroughputUnitsPerSecond));
        Assert.True(zeroDurationStats.ThroughputUnitsPerSecond > 0);

        // The oracle's fallback throughput must match production's exactly (sum of sample
        // durations, not mean), so a regression in either implementation is caught here.
        Trackdub.Benchmarks.Metrics.LatencyStatistics production =
            Trackdub.Benchmarks.Metrics.PercentileCalculator.Calculate(samples, totalUnits: 10, totalDurationSeconds: 0);
        Assert.Equal(production.ThroughputUnitsPerSecond, zeroDurationStats.ThroughputUnitsPerSecond, precision: 10);
    }

    [Fact]
    public void R1_T2_06_NoUnits_ThroughputIsZeroMatchingProduction()
    {
        double[] samples = [50.0, 60.0];

        LatencyStatistics oracle = BenchmarkCalculationOracle.CalculatePercentiles(
            samples, totalUnits: 0, totalDurationSeconds: 0);
        Trackdub.Benchmarks.Metrics.LatencyStatistics production =
            Trackdub.Benchmarks.Metrics.PercentileCalculator.Calculate(samples, totalUnits: 0, totalDurationSeconds: 0);

        Assert.Equal(0.0, oracle.ThroughputUnitsPerSecond);
        Assert.Equal(production.ThroughputUnitsPerSecond, oracle.ThroughputUnitsPerSecond);
    }

    // =========================================================================
    // Requirement 2 Boundaries: Memory & Telemetry (5 tests)
    // =========================================================================

    [Fact]
    public void R2_T2_01_ZeroAllocationWorkload_DeltaIsZeroWithoutError()
    {
        var start = new ResourceTelemetrySnapshot(Mb(100), Mb(100), Mb(50), 0, 0, 0);
        var end = new ResourceTelemetrySnapshot(Mb(100), Mb(100), Mb(50), 0, 0, 0);

        ResourceTelemetryDelta delta = BenchmarkCalculationOracle.CalculateTelemetryDelta(start, end);

        Assert.Equal(0, delta.WorkingSetDeltaBytes);
        Assert.Equal(0, delta.ManagedAllocatedBytes);
        Assert.Equal(0, delta.Gen0Collections);
        Assert.Equal(0, delta.Gen1Collections);
        Assert.Equal(0, delta.Gen2Collections);
    }

    [Fact]
    public void R2_T2_02_MassiveAllocations_HandlesMultiGigabyteValuesWithoutOverflow()
    {
        long sixteenGb = 16L * 1024 * 1024 * 1024;
        long twentyGb = 20L * 1024 * 1024 * 1024;

        var start = new ResourceTelemetrySnapshot(sixteenGb, sixteenGb, sixteenGb, 10, 5, 2);
        var end = new ResourceTelemetrySnapshot(twentyGb, twentyGb, twentyGb, 20, 10, 5);

        ResourceTelemetryDelta delta = BenchmarkCalculationOracle.CalculateTelemetryDelta(start, end);

        long fourGb = 4L * 1024 * 1024 * 1024;
        Assert.Equal(fourGb, delta.WorkingSetDeltaBytes);
        Assert.Equal(fourGb, delta.ManagedAllocatedBytes);
        Assert.Equal(twentyGb, delta.PeakWorkingSetBytes);
    }

    [Fact]
    public void R2_T2_03_HighGenGcCollections_TracksFrequentCollectionsAccurately()
    {
        var start = new ResourceTelemetrySnapshot(Mb(100), Mb(100), Mb(50), 100, 10, 1);
        var end = new ResourceTelemetrySnapshot(Mb(120), Mb(130), Mb(250), 10_100, 510, 51);

        ResourceTelemetryDelta delta = BenchmarkCalculationOracle.CalculateTelemetryDelta(start, end);

        Assert.Equal(10_000, delta.Gen0Collections);
        Assert.Equal(500, delta.Gen1Collections);
        Assert.Equal(50, delta.Gen2Collections);
    }

    [Fact]
    public void R2_T2_04_NegativeWorkingSetDelta_WorkingSetDropRecordedCorrectly()
    {
        // Working set dropped after GC collection during unmanaged cleanup
        var start = new ResourceTelemetrySnapshot(Mb(300), Mb(350), Mb(100), 0, 0, 0);
        var end = new ResourceTelemetrySnapshot(Mb(200), Mb(350), Mb(110), 1, 1, 1);

        ResourceTelemetryDelta delta = BenchmarkCalculationOracle.CalculateTelemetryDelta(start, end);

        Assert.Equal(-Mb(100), delta.WorkingSetDeltaBytes);
        Assert.Equal(Mb(300), delta.PeakWorkingSetBytes);
        Assert.Equal(Mb(10), delta.ManagedAllocatedBytes);
    }

    [Fact]
    public void R2_T2_05_NullSnapshotThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() =>
            BenchmarkCalculationOracle.CalculateTelemetryDelta(null!, new ResourceTelemetrySnapshot(0, 0, 0, 0, 0, 0)));
        Assert.Throws<ArgumentNullException>(() =>
            BenchmarkCalculationOracle.CalculateTelemetryDelta(new ResourceTelemetrySnapshot(0, 0, 0, 0, 0, 0), null!));
    }

    // =========================================================================
    // Requirement 3 Boundaries: Execution Provider Comparisons (5 tests)
    // =========================================================================

    [Fact]
    public void R3_T2_01_SingleProvider_BaselineComparisonHasSpeedupOfOneAndDeltaZero()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "single-ep",
            baselineProvider: "cpu",
            providerStats: stats);

        Assert.Single(report.Comparisons);
        ProviderComparisonMetrics c = report.Comparisons[0];
        Assert.Equal("cpu", c.Provider);
        Assert.Equal(1.0, c.SpeedupFactor);
        Assert.Equal(0.0, c.LatencyDeltaMilliseconds);
        Assert.Equal(1.0, c.ThroughputRatio);
        Assert.Equal(0, c.PeakWorkingSetDeltaBytes);
        Assert.Equal(0, c.ManagedAllocatedDeltaBytes);
    }

    [Fact]
    public void R3_T2_02_MissingBaselineProvider_ThrowsArgumentException()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(800), ManagedAlloc: Mb(40)),
        };

        Assert.Throws<ArgumentException>(() =>
            BenchmarkCalculationOracle.CompareProviders(
                scenario: "missing-baseline",
                baselineProvider: "cpu", // cpu is missing from stats
                providerStats: stats));
    }

    [Fact]
    public void R3_T2_03_ProviderEquivalenceNormalization_MatchesDirectMlAndDmlAndCpuSuffixed()
    {
        BenchmarkEvidenceReport dmlReport = SampleEvidence(10) with
        {
            RequestedProvider = "DirectMl",
            ActualProvider = "dml",
        };
        BenchmarkEvidenceReport cpuReport = SampleEvidence(10) with
        {
            RequestedProvider = "CPUExecutionProvider",
            ActualProvider = "cpu",
        };
        BenchmarkEvidenceReport trtReport = SampleEvidence(10) with
        {
            RequestedProvider = "TensorRTRtx",
            ActualProvider = "tensor-rt-rtx",
        };

        Assert.Single(BenchmarkComparison.Compare([dmlReport]).Accepted);
        Assert.Single(BenchmarkComparison.Compare([cpuReport]).Accepted);
        Assert.Single(BenchmarkComparison.Compare([trtReport]).Accepted);
    }

    [Fact]
    public void R3_T2_04_ZeroLatencyProtection_PreventsDivideByZero()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 0.0, Throughput: 0.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 0.0, Throughput: 0.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "zero-latency",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics c = report.Comparisons.First(p => p.Provider == "directml");
        Assert.False(double.IsInfinity(c.SpeedupFactor));
        Assert.False(double.IsNaN(c.SpeedupFactor));
    }

    [Fact]
    public void R3_T2_06_ZeroBaselineWithPositiveTarget_MatchesProductionSpeedupFallback()
    {
        // Baseline P50 is 0 (e.g. a degenerate/failed measurement) but the target provider has a
        // real, positive P50. Production falls back to SpeedupFactor 1.0 in this case rather than
        // computing 0/positive; the oracle must match that exactly, not just avoid NaN/Infinity.
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 0.0, Throughput: 0.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport oracleReport = BenchmarkCalculationOracle.CompareProviders(
            scenario: "zero-baseline", baselineProvider: "cpu", providerStats: stats);
        Trackdub.Benchmarks.Scenarios.ExecutionProviderMatrixReport productionReport =
            Trackdub.Benchmarks.Scenarios.ExecutionProviderMatrixRunner.CompareProviders(
                scenario: "zero-baseline", baselineProvider: "cpu", providerStats: stats);

        ProviderComparisonMetrics oracleDml = oracleReport.Comparisons.First(p => p.Provider == "directml");
        Trackdub.Benchmarks.Scenarios.ProviderComparisonMetrics productionDml =
            productionReport.Comparisons.First(p => p.Provider == "directml");

        Assert.Equal(1.0, oracleDml.SpeedupFactor);
        Assert.Equal(1.0, oracleDml.ThroughputRatio);
        Assert.Equal(productionDml.SpeedupFactor, oracleDml.SpeedupFactor);
        Assert.Equal(productionDml.ThroughputRatio, oracleDml.ThroughputRatio);
    }

    [Fact]
    public void R3_T2_05_BenchmarkComparison_RejectsFallbackRuns()
    {
        var fallbackReport = SampleEvidence(100.0) with
        {
            RequestedProvider = "cuda",
            ActualProvider = "cpu", // Fell back to CPU!
        };

        BenchmarkComparisonResult comparison = BenchmarkComparison.Compare([fallbackReport]);

        Assert.Empty(comparison.Accepted);
        Assert.Contains(fallbackReport.RunId, comparison.Rejected.Keys);
        Assert.Contains("Requested provider did not execute", comparison.Rejected[fallbackReport.RunId]);
    }

    // =========================================================================
    // Requirement 4 Boundaries: Report Export & Formatting (5 tests)
    // =========================================================================

    [Fact]
    public async Task R4_T2_01_MissingOutputDirectory_WriterCreatesDirectoryAutomatically()
    {
        string root = Path.Join(Path.GetTempPath(), $"dir_test_{Guid.NewGuid():N}");
        string nestedReportPath = Path.Join(root, "level1", "level2", "stage-matrix.json");

        try
        {
            var report = new ControlledStageBenchmarkMatrixReport
            {
                FixturePath = "test.mp4",
                Results = [],
                Status = BenchmarkEvidenceStatus.Completed,
                StartedAtUtc = DateTimeOffset.UtcNow,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                ReportPath = nestedReportPath,
            };

            await BenchmarkReportWriter.WriteAsync(report, CancellationToken.None);

            Assert.True(File.Exists(nestedReportPath), "Report file must exist in nested directory.");
            string content = await File.ReadAllTextAsync(nestedReportPath);
            Assert.Contains("\"FixturePath\": \"test.mp4\"", content);
        }
        finally
        {
            if (Directory.Exists(root))
            {
                Directory.Delete(root, recursive: true);
            }
        }
    }

    [Fact]
    public void R4_T2_02_EmptyReport_SerializesEmptyCollectionsSafely()
    {
        var emptyReport = new ControlledStageBenchmarkMatrixReport
        {
            FixturePath = string.Empty,
            Results = [],
            Status = BenchmarkEvidenceStatus.PartiallyCompleted,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        string json = JsonSerializer.Serialize(emptyReport, BenchmarkReportWriter.SerializerOptions);
        var deserialized = JsonSerializer.Deserialize<ControlledStageBenchmarkMatrixReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(deserialized);
        Assert.Empty(deserialized.Results);
        Assert.Equal(BenchmarkEvidenceStatus.PartiallyCompleted, deserialized.Status);
    }

    [Fact]
    public void R4_T2_03_SpecialCharactersInScenario_MarkdownTableEscapesPipes()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "test|scenario<with>special/chars",
            baselineProvider: "cpu",
            providerStats: stats);

        string markdown = BenchmarkCalculationOracle.FormatMarkdownSummary(report);

        Assert.Contains("test|scenario<with>special/chars", markdown);
        Assert.Contains("| `cpu` |", markdown);
    }

    [Fact]
    public void R4_T2_04_QualityGateViolations_CorrectlyFlagsRegressions()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 80.0, Throughput: 12.5, PeakMemory: Mb(600), ManagedAlloc: Mb(50)), // Only 1.25x speedup
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "regression-check",
            baselineProvider: "cpu",
            providerStats: stats);

        var rule = new QualityGateRule(
            MinSpeedupFactor: 2.0); // Demands 2.0x speedup

        QualityGateResult result = BenchmarkCalculationOracle.EvaluateQualityGate(report, rule);

        Assert.False(result.Passed, "DirectML speedup (1.25x) does not meet requirement (2.0x).");
        Assert.Single(result.Violations);
        Assert.Contains("below required 2", result.Violations[0]);
    }

    [Fact]
    public void R4_T2_05_CorruptedJsonHandling_FailsFastOnInvalidReportStream()
    {
        string corruptedJson = "{ \"FixturePath\": \"corrupted\", \"Status\": \"Completed\", ";

        Assert.Throws<JsonException>(() =>
            JsonSerializer.Deserialize<ControlledStageBenchmarkMatrixReport>(corruptedJson, BenchmarkReportWriter.SerializerOptions));
    }

    private static BenchmarkEvidenceReport SampleEvidence(double milliseconds) => new()
    {
        RunId = Guid.NewGuid(),
        Kind = BenchmarkEvidenceKind.Benchmark,
        Scenario = "Asr",
        RunMode = "fresh-process",
        Status = BenchmarkEvidenceStatus.Completed,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        FixtureSha256 = new string('a', 64),
        RequestedProvider = "Cpu",
        ActualProvider = "Cpu",
        TimingsMilliseconds = new Dictionary<string, double?> { ["pipeline"] = milliseconds },
    };

    private static long Mb(long megabytes) => megabytes * 1024L * 1024L;
}
