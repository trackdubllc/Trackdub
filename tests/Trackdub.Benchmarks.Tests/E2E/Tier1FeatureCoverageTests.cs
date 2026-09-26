using System.Text.Json;
using Trackdub.Benchmarks.Tests.E2E.Contracts;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Benchmarks.Tests.E2E.Oracles;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.E2E;

/// <summary>
/// Tier 1: Isolated feature coverage tests for requirements R1, R2, R3, R4 (>=5 per requirement).
/// </summary>
public sealed class Tier1FeatureCoverageTests
{
    // =========================================================================
    // Requirement 1: Stage-by-Stage Latency and Throughput Profiling (5 tests)
    // =========================================================================

    [Fact]
    public void R1_T1_01_FivePointSummary_ComputesAccurateMinMaxMean()
    {
        double[] samples = [10.0, 20.0, 30.0, 40.0, 50.0];

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(samples);

        Assert.Equal(10.0, stats.MinMilliseconds);
        Assert.Equal(50.0, stats.MaxMilliseconds);
        Assert.Equal(30.0, stats.MeanMilliseconds);
        Assert.Equal(5, stats.SampleCount);
    }

    [Fact]
    public void R1_T1_02_PercentileRankInterpolation_ComputesP50P90P99()
    {
        // 101 samples from 0.0 to 100.0: sample[i] = i
        double[] samples = Enumerable.Range(0, 101).Select(i => (double)i).ToArray();

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(samples);

        Assert.Equal(50.0, stats.P50Milliseconds);
        Assert.Equal(90.0, stats.P90Milliseconds);
        Assert.Equal(99.0, stats.P99Milliseconds);
    }

    [Fact]
    public void R1_T1_03_ThroughputCalculation_ComputesUnitsPerSecond()
    {
        double[] samples = [100.0, 100.0, 100.0, 100.0];
        double totalUnits = 60.0; // 60 audio seconds
        double totalWallClockSeconds = 1.0;

        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(
            samples,
            totalUnits: totalUnits,
            totalDurationSeconds: totalWallClockSeconds);

        Assert.Equal(60.0, stats.ThroughputUnitsPerSecond);
    }

    [Fact]
    public void R1_T1_04_MultiRunStageLatency_AggregatesFiveKeyStages()
    {
        var stageDurations = new Dictionary<string, double>
        {
            ["audio-prep"] = 12.5,
            ["separation"] = 45.0,
            ["transcription"] = 80.0,
            ["alignment"] = 30.0,
            ["dubbing"] = 65.0,
        };

        BenchmarkEvidenceReport report = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "full-pipeline",
            provider: "cpu",
            stageDurations: stageDurations);

        Assert.Equal(5, report.Stages.Count);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);

        string[] expectedStages = ["audio-prep", "separation", "transcription", "alignment", "dubbing"];
        foreach (string stageName in expectedStages)
        {
            BenchmarkEvidenceStage? stage = report.Stages.FirstOrDefault(s => s.Name == stageName);
            Assert.NotNull(stage);
            Assert.Equal(stageDurations[stageName], stage.DurationMilliseconds);
            Assert.Equal(BenchmarkEvidenceStatus.Completed, stage.Status);
        }

        Assert.Equal(232.5, report.TimingsMilliseconds["pipeline"]);
    }

    [Fact]
    public void R1_T1_05_PercentileReportIntegration_PersistsInEvidenceDictionary()
    {
        double[] asrSamples = [80.0, 85.0, 90.0, 95.0, 100.0];
        LatencyStatistics asrStats = BenchmarkCalculationOracle.CalculatePercentiles(asrSamples);

        var timings = new Dictionary<string, double?>
        {
            ["stage:asr:min"] = asrStats.MinMilliseconds,
            ["stage:asr:max"] = asrStats.MaxMilliseconds,
            ["stage:asr:mean"] = asrStats.MeanMilliseconds,
            ["stage:asr:p50"] = asrStats.P50Milliseconds,
            ["stage:asr:p90"] = asrStats.P90Milliseconds,
            ["stage:asr:p99"] = asrStats.P99Milliseconds,
        };

        Assert.Equal(80.0, timings["stage:asr:min"]);
        Assert.Equal(100.0, timings["stage:asr:max"]);
        Assert.Equal(90.0, timings["stage:asr:mean"]);
        Assert.Equal(90.0, timings["stage:asr:p50"]);
        Assert.Equal(98.0, timings["stage:asr:p90"]);
    }

    // =========================================================================
    // Requirement 2: Memory and Resource Telemetry (5 tests)
    // =========================================================================

    [Fact]
    public void R2_T1_01_CaptureProcess_CapturesPeakWorkingSetAndAllocations()
    {
        ResourceTelemetrySnapshot snapshot = BenchmarkCalculationOracle.CaptureProcessTelemetry();

        Assert.True(snapshot.WorkingSetBytes > 0, "Working set bytes must be greater than zero.");
        Assert.True(snapshot.PeakWorkingSetBytes >= snapshot.WorkingSetBytes, "Peak working set must be >= working set.");
        Assert.True(snapshot.ManagedAllocatedBytes > 0, "Managed allocated bytes must be positive.");
        Assert.True(snapshot.Gen0Collections >= 0);
        Assert.True(snapshot.Gen1Collections >= 0);
        Assert.True(snapshot.Gen2Collections >= 0);
    }

    [Fact]
    public void R2_T1_02_CalculateDelta_ComputesWorkingSetAndManagedAllocations()
    {
        var start = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 100_000_000,
            PeakWorkingSetBytes: 120_000_000,
            ManagedAllocatedBytes: 50_000_000,
            Gen0Collections: 10,
            Gen1Collections: 2,
            Gen2Collections: 0);

        var end = new ResourceTelemetrySnapshot(
            WorkingSetBytes: 140_000_000,
            PeakWorkingSetBytes: 150_000_000,
            ManagedAllocatedBytes: 85_000_000,
            Gen0Collections: 14,
            Gen1Collections: 3,
            Gen2Collections: 1);

        ResourceTelemetryDelta delta = BenchmarkCalculationOracle.CalculateTelemetryDelta(start, end);

        Assert.Equal(40_000_000, delta.WorkingSetDeltaBytes);
        Assert.Equal(150_000_000, delta.PeakWorkingSetBytes);
        Assert.Equal(35_000_000, delta.ManagedAllocatedBytes);
        Assert.Equal(4, delta.Gen0Collections);
        Assert.Equal(1, delta.Gen1Collections);
        Assert.Equal(1, delta.Gen2Collections);
    }

    [Fact]
    public void R2_T1_03_CalculateDelta_TracksGen012GcCollections()
    {
        var start = new ResourceTelemetrySnapshot(0, 0, 0, Gen0Collections: 5, Gen1Collections: 2, Gen2Collections: 1);
        var end = new ResourceTelemetrySnapshot(0, 0, 0, Gen0Collections: 12, Gen1Collections: 4, Gen2Collections: 2);

        ResourceTelemetryDelta delta = BenchmarkCalculationOracle.CalculateTelemetryDelta(start, end);

        Assert.Equal(7, delta.Gen0Collections);
        Assert.Equal(2, delta.Gen1Collections);
        Assert.Equal(1, delta.Gen2Collections);
    }

    [Fact]
    public void R2_T1_04_StageLevelMemoryDelta_MeasuresIsolatedStageAllocations()
    {
        var prepStart = new ResourceTelemetrySnapshot(Mb(100), Mb(100), Mb(10), 0, 0, 0);
        var prepEnd = new ResourceTelemetrySnapshot(Mb(120), Mb(125), Mb(25), 1, 0, 0);
        ResourceTelemetryDelta prepDelta = BenchmarkCalculationOracle.CalculateTelemetryDelta(prepStart, prepEnd);

        var asrStart = prepEnd;
        var asrEnd = new ResourceTelemetrySnapshot(Mb(180), Mb(190), Mb(60), 3, 1, 0);
        ResourceTelemetryDelta asrDelta = BenchmarkCalculationOracle.CalculateTelemetryDelta(asrStart, asrEnd);

        Assert.Equal(Mb(15), prepDelta.ManagedAllocatedBytes);
        Assert.Equal(Mb(35), asrDelta.ManagedAllocatedBytes);
        Assert.True(asrDelta.PeakWorkingSetBytes > prepDelta.PeakWorkingSetBytes);
    }

    [Fact]
    public void R2_T1_05_MemoryReportIntegration_PersistsInEvidenceMemoryBytes()
    {
        BenchmarkEvidenceReport report = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "telemetry-check",
            provider: "cpu",
            stageDurations: new Dictionary<string, double> { ["prep"] = 10.0 },
            workingSetStartBytes: 100_000_000,
            workingSetEndBytes: 130_000_000,
            peakWorkingSetBytes: 140_000_000,
            managedAllocatedBytes: 15_000_000,
            gen0: 4,
            gen1: 2,
            gen2: 0);

        Assert.Equal(100_000_000, report.MemoryBytes["processWorkingSetStart"]);
        Assert.Equal(130_000_000, report.MemoryBytes["processWorkingSetEnd"]);
        Assert.Equal(140_000_000, report.MemoryBytes["peakWorkingSetBytes"]);
        Assert.Equal(15_000_000, report.MemoryBytes["managedAllocatedBytes"]);
        Assert.Equal(4, report.MemoryBytes["gen0Collections"]);
    }

    // =========================================================================
    // Requirement 3: Execution Provider Comparison and Matrix Benchmarking (5 tests)
    // =========================================================================

    [Fact]
    public void R3_T1_01_CompareProviders_CalculatesSpeedupFactor()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(800), ManagedAlloc: Mb(40)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "asr-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.Equal(4.0, dml.SpeedupFactor);
    }

    [Fact]
    public void R3_T1_02_CompareProviders_CalculatesLatencyDeltaMilliseconds()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["tensorrt"] = (P50: 25.0, Throughput: 40.0, PeakMemory: Mb(1200), ManagedAlloc: Mb(30)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "asr-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");
        Assert.Equal(-175.0, trt.LatencyDeltaMilliseconds);
    }

    [Fact]
    public void R3_T1_03_CompareProviders_CalculatesThroughputRatio()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 40.0, Throughput: 25.0, PeakMemory: Mb(700), ManagedAlloc: Mb(45)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "tts-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.Equal(2.5, dml.ThroughputRatio);
    }

    [Fact]
    public void R3_T1_04_CompareProviders_CalculatesResourceTradeoffs()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(100)),
            ["tensorrt"] = (P50: 40.0, Throughput: 25.0, PeakMemory: Mb(1200), ManagedAlloc: Mb(80)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "separation-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");
        Assert.Equal(Mb(700), trt.PeakWorkingSetDeltaBytes);
        Assert.Equal(-Mb(20), trt.ManagedAllocatedDeltaBytes);
    }

    [Fact]
    public void R3_T1_05_ProviderMatrixReport_StructureAndMetadataIntegrity()
    {
        DateTimeOffset timestamp = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(600), ManagedAlloc: Mb(50)),
            ["tensorrt"] = (P50: 20.0, Throughput: 50.0, PeakMemory: Mb(900), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "matrix-metadata-test",
            baselineProvider: "cpu",
            providerStats: stats,
            timestamp: timestamp);

        Assert.Equal("matrix-metadata-test", report.Scenario);
        Assert.Equal("cpu", report.BaselineProvider);
        Assert.Equal(timestamp, report.Timestamp);
        Assert.Equal(3, report.Comparisons.Count);
        Assert.Contains(report.Comparisons, c => c.Provider == "cpu" && Math.Abs(c.SpeedupFactor - 1.0) < 1e-9);
    }

    // =========================================================================
    // Requirement 4: Automated Report Export and CI Integration (5 tests)
    // =========================================================================

    [Fact]
    public void R4_T1_01_JsonExport_SerializesCompleteStageMatrixReport()
    {
        ControlledStageBenchmarkMatrixReport report = MockDubbingBenchmarkHarness.CreateMockStageMatrixReport(
            fixturePath: "fixture.mp4",
            provider: "cpu",
            stageDurations: new Dictionary<string, double>
            {
                ["prep"] = 10.0,
                ["asr"] = 50.0,
            });

        string json = JsonSerializer.Serialize(report, BenchmarkReportWriter.SerializerOptions);

        Assert.False(string.IsNullOrWhiteSpace(json));
        Assert.Contains("\"FixturePath\": \"fixture.mp4\"", json);
        Assert.Contains("\"Status\": \"Completed\"", json);
        Assert.Contains("\"Stage\": \"asr\"", json);
    }

    [Fact]
    public void R4_T1_02_JsonExport_RoundtripsWithoutDataLoss()
    {
        ControlledStageBenchmarkMatrixReport original = MockDubbingBenchmarkHarness.CreateMockStageMatrixReport(
            fixturePath: "fixture.mp4",
            provider: "cpu",
            stageDurations: new Dictionary<string, double>
            {
                ["prep"] = 15.5,
            });

        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        var deserialized = JsonSerializer.Deserialize<ControlledStageBenchmarkMatrixReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.FixturePath, deserialized.FixturePath);
        Assert.Equal(original.Status, deserialized.Status);
        Assert.Single(deserialized.Results);
        Assert.Equal("prep", deserialized.Results[0].Stage);
    }

    [Fact]
    public void R4_T1_03_MarkdownExport_GeneratesValidTableFormat()
    {
        ControlledStageBenchmarkMatrixReport report = MockDubbingBenchmarkHarness.CreateMockStageMatrixReport(
            fixturePath: "fixture.mp4",
            provider: "cpu",
            stageDurations: new Dictionary<string, double>
            {
                ["prep"] = 10.0,
                ["asr"] = 50.0,
            });

        string markdown = BenchmarkCalculationOracle.FormatMarkdownSummary(report);

        Assert.Contains("# Controlled Stage Matrix Summary", markdown);
        Assert.Contains("| Stage | Status | Duration (ms) | Provider | Model |", markdown);
        Assert.Contains("|:---|:---|---:|:---|:---|", markdown);
        Assert.Contains("| `prep` | `Completed` |", markdown);
        Assert.Contains("| `asr` | `Completed` |", markdown);
    }

    [Fact]
    public void R4_T1_04_MarkdownExport_FormatsProviderComparisonMatrix()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 25.0, Throughput: 40.0, PeakMemory: Mb(700), ManagedAlloc: Mb(40)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "asr-pipeline",
            baselineProvider: "cpu",
            providerStats: stats);

        string markdown = BenchmarkCalculationOracle.FormatMarkdownSummary(report);

        Assert.Contains("# Benchmark Matrix Summary: asr-pipeline", markdown);
        Assert.Contains("| Provider | P50 Latency (ms) | Speedup | Latency Delta (ms) |", markdown);
        Assert.Contains("`directml`", markdown);
        Assert.Contains("4.00x :rocket:", markdown);
    }

    [Fact]
    public void R4_T1_05_CiQualityGate_EvaluatesPerformanceBudgets()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 45.0, Throughput: 22.0, PeakMemory: Mb(700), ManagedAlloc: Mb(40)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "gate-test",
            baselineProvider: "cpu",
            providerStats: stats);

        var rule = new QualityGateRule(
            MaxP50LatencyMilliseconds: 50.0,
            MinSpeedupFactor: 2.0);

        QualityGateResult result = BenchmarkCalculationOracle.EvaluateQualityGate(report, rule);

        Assert.True(result.Passed, "DirectML meets the P50 < 50ms and Speedup >= 2.0x quality gate.");
        Assert.Empty(result.Violations);
    }

    private static long Mb(long megabytes) => megabytes * 1024L * 1024L;
}
