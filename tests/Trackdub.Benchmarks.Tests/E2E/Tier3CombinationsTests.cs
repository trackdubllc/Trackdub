using System.Text.Json;
using Trackdub.Benchmarks.Tests.E2E.Contracts;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Benchmarks.Tests.E2E.Oracles;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.E2E;

/// <summary>
/// Tier 3: Cross-feature pairwise and multi-feature combination tests.
/// </summary>
public sealed class Tier3CombinationsTests
{
    [Fact]
    public void R1_R2_Combination_MultiRunStageLatencyWithPeakMemoryTracking()
    {
        // Simulate 5 runs across 5 stages, collecting latency samples and telemetry
        string[] stages = ["audio-prep", "separation", "transcription", "alignment", "dubbing"];
        var stageSamples = new Dictionary<string, List<double>>();
        foreach (string stage in stages)
        {
            stageSamples[stage] = [];
        }

        // 5 simulated iteration passes
        double[][] iterations =
        [
            [12.0, 45.0, 80.0, 30.0, 60.0],
            [13.0, 44.0, 82.0, 31.0, 62.0],
            [11.5, 46.0, 79.0, 29.5, 59.0],
            [14.0, 45.5, 85.0, 32.0, 65.0],
            [12.5, 43.5, 81.0, 30.5, 61.0],
        ];

        foreach (double[] run in iterations)
        {
            for (int i = 0; i < stages.Length; i++)
            {
                stageSamples[stages[i]].Add(run[i]);
            }
        }

        // R1: Calculate percentiles for each stage
        var stageStats = new Dictionary<string, LatencyStatistics>();
        foreach (string stage in stages)
        {
            stageStats[stage] = BenchmarkCalculationOracle.CalculatePercentiles(stageSamples[stage]);
        }

        // R2: Memory telemetry tracking across the multi-run
        var startMem = new ResourceTelemetrySnapshot(Mb(100), Mb(100), Mb(20), 0, 0, 0);
        var endMem = new ResourceTelemetrySnapshot(Mb(180), Mb(200), Mb(95), 6, 2, 1);
        ResourceTelemetryDelta memDelta = BenchmarkCalculationOracle.CalculateTelemetryDelta(startMem, endMem);

        // Verification
        Assert.Equal(81.0, stageStats["transcription"].P50Milliseconds);
        Assert.Equal(85.0, stageStats["transcription"].MaxMilliseconds);
        Assert.Equal(79.0, stageStats["transcription"].MinMilliseconds);

        Assert.Equal(Mb(80), memDelta.WorkingSetDeltaBytes);
        Assert.Equal(Mb(180), memDelta.PeakWorkingSetBytes);
        Assert.Equal(Mb(75), memDelta.ManagedAllocatedBytes);
        Assert.Equal(6, memDelta.Gen0Collections);
    }

    [Fact]
    public void R2_R3_Combination_MultiProviderMatrixWithResourceTradeoffs()
    {
        // R3 provider comparison with R2 memory metrics
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 250.0, Throughput: 4.0, PeakMemory: Mb(400), ManagedAlloc: Mb(80)),
            ["directml"] = (P50: 75.0, Throughput: 13.3, PeakMemory: Mb(750), ManagedAlloc: Mb(60)),
            ["tensorrt"] = (P50: 35.0, Throughput: 28.5, PeakMemory: Mb(1100), ManagedAlloc: Mb(45)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "voice-separation-matrix",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");

        // Speedup assertions
        Assert.True(dml.SpeedupFactor > 3.0, "DirectML speedup should exceed 3x.");
        Assert.True(trt.SpeedupFactor > 7.0, "TensorRT speedup should exceed 7x.");

        // Resource trade-off assertions: GPU providers require higher peak working set (VRAM buffers)
        Assert.Equal(Mb(350), dml.PeakWorkingSetDeltaBytes);
        Assert.Equal(Mb(700), trt.PeakWorkingSetDeltaBytes);

        // But lower managed allocation pressure on the .NET runtime
        Assert.True(dml.ManagedAllocatedDeltaBytes < 0);
        Assert.True(trt.ManagedAllocatedDeltaBytes < 0);
    }

    [Fact]
    public void R1_R3_Combination_MultiProviderStagePercentiles()
    {
        // Stage-level percentiles for CPU vs DirectML
        double[] cpuSamples = [180.0, 190.0, 200.0, 210.0, 220.0];
        double[] dmlSamples = [45.0, 48.0, 50.0, 52.0, 55.0];

        LatencyStatistics cpuStats = BenchmarkCalculationOracle.CalculatePercentiles(cpuSamples);
        LatencyStatistics dmlStats = BenchmarkCalculationOracle.CalculatePercentiles(dmlSamples);

        double speedupP50 = cpuStats.P50Milliseconds / dmlStats.P50Milliseconds;
        double speedupP90 = cpuStats.P90Milliseconds / dmlStats.P90Milliseconds;

        Assert.Equal(200.0, cpuStats.P50Milliseconds);
        Assert.Equal(50.0, dmlStats.P50Milliseconds);
        Assert.Equal(4.0, speedupP50);
        Assert.True(speedupP90 >= 3.8);
    }

    [Fact]
    public void R1_R2_R4_Combination_FullTelemetryJsonAndMarkdownExport()
    {
        var stageDurations = new Dictionary<string, double>
        {
            ["audio-prep"] = 10.0,
            ["separation"] = 40.0,
            ["transcription"] = 75.0,
            ["alignment"] = 25.0,
            ["dubbing"] = 50.0,
        };

        BenchmarkEvidenceReport evidence = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "e2e-dubbing",
            provider: "directml",
            stageDurations: stageDurations,
            workingSetStartBytes: Mb(150),
            workingSetEndBytes: Mb(250),
            peakWorkingSetBytes: Mb(280),
            managedAllocatedBytes: Mb(60),
            gen0: 8,
            gen1: 3,
            gen2: 1);

        // JSON serialization
        string json = JsonSerializer.Serialize(evidence, BenchmarkReportWriter.SerializerOptions);
        Assert.Contains("\"ActualProvider\": \"directml\"", json);
        Assert.Contains("\"managedAllocatedBytes\": 62914560", json);
        Assert.Contains("\"stage:transcription:duration\": 75", json);

        // Markdown summary of execution provider matrix
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 400.0, Throughput: 2.5, PeakMemory: Mb(500), ManagedAlloc: Mb(100)),
            ["directml"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(750), ManagedAlloc: Mb(70)),
        };

        ExecutionProviderMatrixReport matrixReport = BenchmarkCalculationOracle.CompareProviders(
            scenario: "e2e-dubbing",
            baselineProvider: "cpu",
            providerStats: stats);

        string markdown = BenchmarkCalculationOracle.FormatMarkdownSummary(matrixReport);
        Assert.Contains("Benchmark Matrix Summary: e2e-dubbing", markdown);
        Assert.Contains("2.00x :rocket:", markdown);
    }

    [Fact]
    public void R3_R4_CiQualityGateWithProviderMatrix()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 300.0, Throughput: 3.3, PeakMemory: Mb(400), ManagedAlloc: Mb(80)),
            ["directml"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(600), ManagedAlloc: Mb(50)),
            ["tensorrt"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(900), ManagedAlloc: Mb(40)),
        };

        ExecutionProviderMatrixReport report = BenchmarkCalculationOracle.CompareProviders(
            scenario: "ci-regression-gate",
            baselineProvider: "cpu",
            providerStats: stats);

        // Quality gate: require DirectML and TensorRT to have speedup >= 2.5x vs CPU
        var gateRule = new QualityGateRule(
            MinSpeedupFactor: 2.5);

        QualityGateResult gateResult = BenchmarkCalculationOracle.EvaluateQualityGate(report, gateRule);

        Assert.True(gateResult.Passed);
        Assert.Empty(gateResult.Violations);
    }

    [Fact]
    public void R1_R2_R3_R4_ComprehensiveEndToEndMatrixExecution()
    {
        // Comprehensive test covering all 4 requirements in a full simulated matrix run
        var providers = new[] { "cpu", "directml", "tensorrt" };
        var stageP50s = new Dictionary<string, Dictionary<string, double>>
        {
            ["cpu"] = new() { ["prep"] = 20.0, ["asr"] = 150.0, ["tts"] = 100.0 },
            ["directml"] = new() { ["prep"] = 18.0, ["asr"] = 45.0, ["tts"] = 35.0 },
            ["tensorrt"] = new() { ["prep"] = 18.0, ["asr"] = 25.0, ["tts"] = 20.0 },
        };

        var providerSummary = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>();
        foreach (string ep in providers)
        {
            double totalP50 = stageP50s[ep].Values.Sum();
            double throughput = 1000.0 / totalP50;
            long peak = ep == "cpu" ? Mb(400) : (ep == "directml" ? Mb(700) : Mb(1100));
            long alloc = ep == "cpu" ? Mb(60) : (ep == "directml" ? Mb(45) : Mb(35));

            providerSummary[ep] = (totalP50, throughput, peak, alloc);
        }

        ExecutionProviderMatrixReport matrixReport = BenchmarkCalculationOracle.CompareProviders(
            scenario: "comprehensive-matrix",
            baselineProvider: "cpu",
            providerStats: providerSummary);

        // Assert R3
        Assert.Equal(3, matrixReport.Comparisons.Count);
        ProviderComparisonMetrics trt = matrixReport.Comparisons.First(c => c.Provider == "tensorrt");
        Assert.True(trt.SpeedupFactor >= 4.0);

        // Assert R4 Markdown
        string md = BenchmarkCalculationOracle.FormatMarkdownSummary(matrixReport);
        Assert.Contains("comprehensive-matrix", md);
        Assert.Contains("`tensorrt`", md);

        // Assert R4 JSON
        string json = JsonSerializer.Serialize(matrixReport, BenchmarkReportWriter.SerializerOptions);
        Assert.Contains("\"BaselineProvider\": \"cpu\"", json);
    }

    private static long Mb(long megabytes) => megabytes * 1024L * 1024L;
}
