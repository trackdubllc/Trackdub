using Trackdub.Benchmarks.Scenarios;

namespace Trackdub.Benchmarks.Tests.Scenarios;

public sealed class ExecutionProviderMatrixCalculationTests
{
    private static long Mb(long megabytes) => megabytes * 1024L * 1024L;

    [Fact]
    public void CompareProviders_CalculatesAccurateSpeedupFactor()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(800), ManagedAlloc: Mb(40)),
            ["tensorrt"] = (P50: 25.0, Throughput: 40.0, PeakMemory: Mb(1000), ManagedAlloc: Mb(35)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "asr-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");

        Assert.Equal(4.0, dml.SpeedupFactor);
        Assert.Equal(8.0, trt.SpeedupFactor);
    }

    [Fact]
    public void CompareProviders_CalculatesLatencyDeltaMilliseconds_NegativeForImprovement()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(800), ManagedAlloc: Mb(40)),
            ["tensorrt"] = (P50: 25.0, Throughput: 40.0, PeakMemory: Mb(1000), ManagedAlloc: Mb(35)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "asr-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");

        Assert.Equal(-150.0, dml.LatencyDeltaMilliseconds);
        Assert.Equal(-175.0, trt.LatencyDeltaMilliseconds);
    }

    [Fact]
    public void CompareProviders_CalculatesThroughputRatio()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 40.0, Throughput: 25.0, PeakMemory: Mb(700), ManagedAlloc: Mb(45)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "tts-model",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.Equal(2.5, dml.ThroughputRatio);
    }

    [Fact]
    public void CompareProviders_CalculatesResourceTradeoffs()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(100)),
            ["tensorrt"] = (P50: 40.0, Throughput: 25.0, PeakMemory: Mb(1200), ManagedAlloc: Mb(80)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "separation",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");
        Assert.Equal(Mb(700), trt.PeakWorkingSetDeltaBytes);
        Assert.Equal(-Mb(20), trt.ManagedAllocatedDeltaBytes);
    }

    [Fact]
    public void CompareProviders_ProducesCompleteMatrixReport_WithMetadata()
    {
        DateTimeOffset timestamp = new(2026, 9, 25, 12, 0, 0, TimeSpan.Zero);
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(600), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "full-pipeline",
            baselineProvider: "cpu",
            providerStats: stats,
            timestamp: timestamp);

        Assert.Equal("full-pipeline", report.Scenario);
        Assert.Equal("cpu", report.BaselineProvider);
        Assert.Equal(timestamp, report.Timestamp);
        Assert.Equal(2, report.Comparisons.Count);

        ProviderComparisonMetrics cpu = report.Comparisons.First(c => c.Provider == "cpu");
        Assert.Equal(1.0, cpu.SpeedupFactor);
        Assert.Equal(0.0, cpu.LatencyDeltaMilliseconds);
        Assert.Equal(1.0, cpu.ThroughputRatio);
        Assert.Equal(0, cpu.PeakWorkingSetDeltaBytes);
        Assert.Equal(0, cpu.ManagedAllocatedDeltaBytes);
    }

    [Fact]
    public void CompareProviders_MultiProviderMatrix_IncludesAllConfiguredProviders()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 200.0, Throughput: 5.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(800), ManagedAlloc: Mb(40)),
            ["tensorrt"] = (P50: 25.0, Throughput: 40.0, PeakMemory: Mb(1000), ManagedAlloc: Mb(35)),
            ["openvino"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(600), ManagedAlloc: Mb(45)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "multi-ep",
            baselineProvider: "cpu",
            providerStats: stats);

        Assert.Equal(4, report.Comparisons.Count);
        Assert.Contains(report.Comparisons, c => c.Provider == "cpu");
        Assert.Contains(report.Comparisons, c => c.Provider == "directml");
        Assert.Contains(report.Comparisons, c => c.Provider == "tensorrt");
        Assert.Contains(report.Comparisons, c => c.Provider == "openvino");

        ProviderComparisonMetrics openvino = report.Comparisons.First(c => c.Provider == "openvino");
        Assert.Equal(2.0, openvino.SpeedupFactor);
        Assert.Equal(-100.0, openvino.LatencyDeltaMilliseconds);
        Assert.Equal(2.0, openvino.ThroughputRatio);
    }
}
