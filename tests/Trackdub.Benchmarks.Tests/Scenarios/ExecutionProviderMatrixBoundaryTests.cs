using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Scenarios;

public sealed class ExecutionProviderMatrixBoundaryTests
{
    private static long Mb(long megabytes) => megabytes * 1024L * 1024L;

    [Fact]
    public void IdenticalBaseline_ReturnsIdentityMetrics()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 150.0, Throughput: 12.0, PeakMemory: Mb(600), ManagedAlloc: Mb(75)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "identity-check",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics metric = Assert.Single(report.Comparisons);
        Assert.Equal("cpu", metric.Provider);
        Assert.Equal(1.0, metric.SpeedupFactor);
        Assert.Equal(0.0, metric.LatencyDeltaMilliseconds);
        Assert.Equal(1.0, metric.ThroughputRatio);
        Assert.Equal(0, metric.PeakWorkingSetDeltaBytes);
        Assert.Equal(0, metric.ManagedAllocatedDeltaBytes);
    }

    [Fact]
    public void MissingBaselineProvider_ThrowsArgumentException()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(800), ManagedAlloc: Mb(40)),
        };

        ArgumentException ex = Assert.Throws<ArgumentException>(() =>
            ExecutionProviderMatrixRunner.CompareProviders(
                scenario: "missing-baseline",
                baselineProvider: "cpu",
                providerStats: stats));

        Assert.Contains("cpu", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void DivisionByZeroGuard_TargetP50Zero_ReturnsSafeDefault()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 0.0, Throughput: 0.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "zero-latency",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.False(double.IsInfinity(dml.SpeedupFactor));
        Assert.False(double.IsNaN(dml.SpeedupFactor));
        Assert.Equal(1.0, dml.SpeedupFactor);
    }

    [Fact]
    public void DivisionByZeroGuard_BaselineP50Zero_ReturnsSafeDefault()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 0.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "zero-baseline-latency",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.False(double.IsInfinity(dml.SpeedupFactor));
        Assert.False(double.IsNaN(dml.SpeedupFactor));
        Assert.Equal(1.0, dml.SpeedupFactor);
    }

    [Fact]
    public void DivisionByZeroGuard_BaselineThroughputZero_ReturnsSafeDefault()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 0.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "zero-baseline-throughput",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.False(double.IsInfinity(dml.ThroughputRatio));
        Assert.False(double.IsNaN(dml.ThroughputRatio));
        Assert.Equal(1.0, dml.ThroughputRatio);
    }

    [Fact]
    public void ReverseSpeedup_TargetSlowerThanBaseline_ComputesFractionalSpeedupAndPositiveDelta()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 20.0, PeakMemory: Mb(400), ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 200.0, Throughput: 10.0, PeakMemory: Mb(600), ManagedAlloc: Mb(60)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "slow-ep",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.Equal(0.5, dml.SpeedupFactor);
        Assert.Equal(100.0, dml.LatencyDeltaMilliseconds);
        Assert.Equal(0.5, dml.ThroughputRatio);
    }

    [Fact]
    public void NegativeDeltas_MemorySavings_PreservesSignedValues()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(200)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(400), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "memory-savings",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.Equal(-Mb(100), dml.PeakWorkingSetDeltaBytes);
        Assert.Equal(-Mb(150), dml.ManagedAllocatedDeltaBytes);
    }

    [Fact]
    public void LargeMemoryValues_HandlesMultiGigabyteValuesWithout64BitOverflow()
    {
        long sixteenGb = 16L * 1024L * 1024L * 1024L;
        long twentyFourGb = 24L * 1024L * 1024L * 1024L;

        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: sixteenGb, ManagedAlloc: Mb(50)),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: twentyFourGb, ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "large-memory",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        Assert.Equal(8L * 1024L * 1024L * 1024L, dml.PeakWorkingSetDeltaBytes);
    }

    [Fact]
    public void ProviderCasingAndAliasNormalization_ResolvesEquivalentProviders()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["CPU"] = (P50: 100.0, Throughput: 10.0, PeakMemory: Mb(500), ManagedAlloc: Mb(50)),
            ["DirectML"] = (P50: 50.0, Throughput: 20.0, PeakMemory: Mb(600), ManagedAlloc: Mb(50)),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "case-insensitivity",
            baselineProvider: "cpu",
            providerStats: stats);

        Assert.Equal(2, report.Comparisons.Count);
        ProviderComparisonMetrics dml = report.Comparisons.First(c =>
            string.Equals(c.Provider, "DirectML", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(2.0, dml.SpeedupFactor);
        Assert.Equal(-50.0, dml.LatencyDeltaMilliseconds);
    }

    [Fact]
    public void ProviderFallbackDetection_IdentifiesMismatch()
    {
        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "fallback-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            RequestedProvider = "tensorrt",
            ActualProvider = "cpu",
            Reason = "TensorRT execution provider was not available; fell back to CPU.",
            TimingsMilliseconds = new Dictionary<string, double?>
            {
                ["pipeline:p50"] = 100.0,
                ["pipeline:throughput"] = 10.0,
            },
            MemoryBytes = new Dictionary<string, long?>
            {
                ["peakWorkingSetBytes"] = Mb(500),
                ["managedAllocatedBytes"] = Mb(50),
            },
            Stages = [],
        };

        Assert.NotNull(report.Reason);
        Assert.NotEqual(report.RequestedProvider, report.ActualProvider);
    }
}
