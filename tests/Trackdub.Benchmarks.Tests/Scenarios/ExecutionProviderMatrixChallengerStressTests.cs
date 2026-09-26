using System.Collections.Concurrent;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Scenarios;

public sealed class ExecutionProviderMatrixChallengerStressTests
{
    private const long OneTb = 1024L * 1024L * 1024L * 1024L;

    [Fact]
    public void MicrosecondLatencies_ComputesAccurateSpeedupAndDeltas()
    {
        // 1 microsecond = 0.001 milliseconds
        // 100 nanoseconds = 0.0001 milliseconds
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 10.0, Throughput: 100.0, PeakMemory: 500 * 1024 * 1024L, ManagedAlloc: 50 * 1024 * 1024L),
            ["directml"] = (P50: 0.001, Throughput: 1_000_000.0, PeakMemory: 600 * 1024 * 1024L, ManagedAlloc: 40 * 1024 * 1024L),
            ["tensorrt"] = (P50: 0.0001, Throughput: 10_000_000.0, PeakMemory: 700 * 1024 * 1024L, ManagedAlloc: 30 * 1024 * 1024L),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "extreme-microsecond-latencies",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");

        // 10.0 / 0.001 = 10,000.0x speedup
        Assert.Equal(10000.0, dml.SpeedupFactor, precision: 4);
        // 0.001 - 10.0 = -9.999ms latency delta
        Assert.Equal(-9.999, dml.LatencyDeltaMilliseconds, precision: 6);
        // 1,000,000.0 / 100.0 = 10,000.0 throughput ratio
        Assert.Equal(10000.0, dml.ThroughputRatio, precision: 4);

        // 10.0 / 0.0001 = 100,000.0x speedup
        Assert.Equal(100000.0, trt.SpeedupFactor, precision: 4);
        // 0.0001 - 10.0 = -9.9999ms latency delta
        Assert.Equal(-9.9999, trt.LatencyDeltaMilliseconds, precision: 6);
        // 10,000,000.0 / 100.0 = 100,000.0 throughput ratio
        Assert.Equal(100000.0, trt.ThroughputRatio, precision: 4);
    }

    [Fact]
    public void MassiveSpeedupFactor_10000xSpeedup_HandledAccuratelyWithoutClamping()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 10_000.0, Throughput: 0.1, PeakMemory: 1024L * 1024L * 1024L, ManagedAlloc: 100L * 1024L * 1024L),
            ["gpu_accelerated"] = (P50: 1.0, Throughput: 1000.0, PeakMemory: 2048L * 1024L * 1024L, ManagedAlloc: 50L * 1024L * 1024L),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "massive-speedup",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics gpu = report.Comparisons.First(c => c.Provider == "gpu_accelerated");

        // 10,000.0 / 1.0 = 10,000.0
        Assert.Equal(10000.0, gpu.SpeedupFactor, precision: 6);
        // 1.0 - 10,000.0 = -9999.0ms
        Assert.Equal(-9999.0, gpu.LatencyDeltaMilliseconds, precision: 6);
        // 1000.0 / 0.1 = 10,000.0
        Assert.Equal(10000.0, gpu.ThroughputRatio, precision: 6);
    }

    [Fact]
    public void DegradedPerformance_ExtremeLatencyDegradation_AndPlus50000MsDelta()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 5.0, Throughput: 200.0, PeakMemory: 500 * 1024 * 1024L, ManagedAlloc: 50 * 1024 * 1024L),
            ["degraded_ep"] = (P50: 50_005.0, Throughput: 0.02, PeakMemory: 4000 * 1024 * 1024L, ManagedAlloc: 2000 * 1024 * 1024L),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "extreme-degradation",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics degraded = report.Comparisons.First(c => c.Provider == "degraded_ep");

        // 50,005.0 - 5.0 = +50,000.0ms delta
        Assert.Equal(50000.0, degraded.LatencyDeltaMilliseconds, precision: 6);

        // SpeedupFactor: 5.0 / 50005.0 ≈ 0.0000999900009999 ≈ 0.0001x
        double expectedSpeedup = 5.0 / 50005.0;
        Assert.Equal(expectedSpeedup, degraded.SpeedupFactor, precision: 8);
        Assert.True(degraded.SpeedupFactor < 0.00011 && degraded.SpeedupFactor > 0.00009);

        // ThroughputRatio: 0.02 / 200.0 = 0.0001x
        Assert.Equal(0.0001, degraded.ThroughputRatio, precision: 8);
    }

    [Fact]
    public void ZeroAndNegativeLatencies_FallbacksSafelyWithoutExceptionsOrNan()
    {
        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: 500 * 1024 * 1024L, ManagedAlloc: 50 * 1024 * 1024L),
            ["zero_latency"] = (P50: 0.0, Throughput: 0.0, PeakMemory: 500 * 1024 * 1024L, ManagedAlloc: 50 * 1024 * 1024L),
            ["negative_latency"] = (P50: -5.0, Throughput: -1.0, PeakMemory: 500 * 1024 * 1024L, ManagedAlloc: 50 * 1024 * 1024L),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "zero-and-negative-handling",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics zero = report.Comparisons.First(c => c.Provider == "zero_latency");
        ProviderComparisonMetrics neg = report.Comparisons.First(c => c.Provider == "negative_latency");

        Assert.Equal(1.0, zero.SpeedupFactor);
        Assert.Equal(-100.0, zero.LatencyDeltaMilliseconds);
        Assert.Equal(0.0, zero.ThroughputRatio);

        Assert.Equal(1.0, neg.SpeedupFactor);
        Assert.Equal(-105.0, neg.LatencyDeltaMilliseconds);
        Assert.Equal(-0.1, neg.ThroughputRatio, precision: 6);

        // Baseline zero/negative tests
        var baselineZeroStats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 0.0, Throughput: 0.0, PeakMemory: 100L, ManagedAlloc: 100L),
            ["other"] = (P50: 50.0, Throughput: 20.0, PeakMemory: 100L, ManagedAlloc: 100L),
        };
        ExecutionProviderMatrixReport baseZeroReport = ExecutionProviderMatrixRunner.CompareProviders("base-zero", "cpu", baselineZeroStats);
        Assert.Equal(1.0, baseZeroReport.Comparisons.First(c => c.Provider == "other").SpeedupFactor);
        Assert.Equal(1.0, baseZeroReport.Comparisons.First(c => c.Provider == "other").ThroughputRatio);
    }

    [Fact]
    public void TerabyteScaleMemoryTradeoffs_CalculatesExactSignedDeltasWithout64BitOverflow()
    {
        long fiftyTb = 50L * OneTb;
        long oneHundredTb = 100L * OneTb;
        long thirtyTb = 30L * OneTb;
        long eightyTb = 80L * OneTb;

        var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 100.0, Throughput: 10.0, PeakMemory: fiftyTb, ManagedAlloc: oneHundredTb),
            ["directml"] = (P50: 50.0, Throughput: 20.0, PeakMemory: oneHundredTb, ManagedAlloc: thirtyTb),
            ["tensorrt"] = (P50: 25.0, Throughput: 40.0, PeakMemory: eightyTb, ManagedAlloc: fiftyTb),
        };

        ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "terabyte-scale-bench",
            baselineProvider: "cpu",
            providerStats: stats);

        ProviderComparisonMetrics dml = report.Comparisons.First(c => c.Provider == "directml");
        ProviderComparisonMetrics trt = report.Comparisons.First(c => c.Provider == "tensorrt");

        // PeakWorkingSetDeltaBytes: 100 TB - 50 TB = +50 TB
        Assert.Equal(50L * OneTb, dml.PeakWorkingSetDeltaBytes);
        Assert.Equal(54_975_581_388_800L, dml.PeakWorkingSetDeltaBytes);

        // ManagedAllocatedDeltaBytes: 30 TB - 100 TB = -70 TB
        Assert.Equal(-70L * OneTb, dml.ManagedAllocatedDeltaBytes);
        Assert.Equal(-76_965_813_944_320L, dml.ManagedAllocatedDeltaBytes);

        // TensorRT: Peak: 80 TB - 50 TB = +30 TB; Alloc: 50 TB - 100 TB = -50 TB
        Assert.Equal(30L * OneTb, trt.PeakWorkingSetDeltaBytes);
        Assert.Equal(-50L * OneTb, trt.ManagedAllocatedDeltaBytes);

        // Petabyte scale check (1 PB vs 2 PB)
        long onePb = 1024L * OneTb;
        long twoPb = 2L * onePb;
        var pbStats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
        {
            ["cpu"] = (P50: 10.0, Throughput: 1.0, PeakMemory: onePb, ManagedAlloc: onePb),
            ["gpu"] = (P50: 5.0, Throughput: 2.0, PeakMemory: twoPb, ManagedAlloc: twoPb),
        };
        ExecutionProviderMatrixReport pbReport = ExecutionProviderMatrixRunner.CompareProviders("pb-scale", "cpu", pbStats);
        Assert.Equal(onePb, pbReport.Comparisons.First(c => c.Provider == "gpu").PeakWorkingSetDeltaBytes);
    }

    [Fact]
    public void FiftyConcurrentThreads_ExecuteSimultaneouslyWithRandomDistributions_ThreadSafe()
    {
        const int threadCount = 50;
        const int iterationsPerThread = 200;
        var exceptions = new ConcurrentBag<Exception>();

        Parallel.For(0, threadCount, new ParallelOptions { MaxDegreeOfParallelism = threadCount }, threadId =>
        {
            try
            {
                var random = new Random(unchecked((threadId * 10007) + 42));

                for (int iter = 0; iter < iterationsPerThread; iter++)
                {
                    double baseP50 = (random.NextDouble() * 1000.0) + 0.1;
                    double baseThroughput = (random.NextDouble() * 100.0) + 1.0;
                    long basePeak = (long)(random.NextDouble() * 50 * OneTb) + 1024L;
                    long baseAlloc = (long)(random.NextDouble() * 50 * OneTb) + 1024L;

                    string baselineKey = (iter % 2 == 0) ? "cpu" : "CPU";

                    var stats = new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>(StringComparer.OrdinalIgnoreCase)
                    {
                        [baselineKey] = (baseP50, baseThroughput, basePeak, baseAlloc),
                    };

                    int candidateCount = random.Next(2, 6);
                    for (int c = 0; c < candidateCount; c++)
                    {
                        string providerName = $"provider_{c}_{threadId}";
                        // Draw extreme values across microsecond to degraded
                        double p50 = (c % 4) switch
                        {
                            0 => 0.001 * (random.NextDouble() + 0.01), // microsecond
                            1 => (random.NextDouble() * 50_000.0) + 1000.0, // degraded
                            2 => baseP50 / ((random.NextDouble() * 9999.0) + 1.0), // high speedup
                            _ => (random.NextDouble() * 500.0) + 1.0,
                        };

                        double throughput = (random.NextDouble() * 10_000.0) + 0.01;
                        long peak = (long)(random.NextDouble() * 100 * OneTb);
                        long alloc = (long)(random.NextDouble() * 100 * OneTb);

                        stats[providerName] = (p50, throughput, peak, alloc);
                    }

                    string scenarioName = $"scenario_{threadId}_{iter}";
                    ExecutionProviderMatrixReport report = ExecutionProviderMatrixRunner.CompareProviders(
                        scenarioName,
                        baselineKey,
                        stats);

                    Assert.Equal(scenarioName, report.Scenario);
                    Assert.Equal(stats.Count, report.Comparisons.Count);

                    // Baseline validation
                    ProviderComparisonMetrics baseMetrics = report.Comparisons.First(x =>
                        string.Equals(x.Provider, baselineKey, StringComparison.OrdinalIgnoreCase));
                    Assert.Equal(1.0, baseMetrics.SpeedupFactor);
                    Assert.Equal(0.0, baseMetrics.LatencyDeltaMilliseconds);
                    Assert.Equal(1.0, baseMetrics.ThroughputRatio);
                    Assert.Equal(0L, baseMetrics.PeakWorkingSetDeltaBytes);
                    Assert.Equal(0L, baseMetrics.ManagedAllocatedDeltaBytes);

                    // Candidate mathematical oracle validation
                    foreach (var comparison in report.Comparisons)
                    {
                        var expected = stats[comparison.Provider];
                        Assert.Equal(expected.P50, comparison.P50Milliseconds);

                        double expectedSpeedup = (expected.P50 > 0 && baseP50 > 0)
                            ? baseP50 / expected.P50
                            : 1.0;
                        if (double.IsNaN(expectedSpeedup) || double.IsInfinity(expectedSpeedup))
                        {
                            expectedSpeedup = 1.0;
                        }

                        Assert.Equal(expectedSpeedup, comparison.SpeedupFactor, precision: 5);
                        Assert.Equal(expected.P50 - baseP50, comparison.LatencyDeltaMilliseconds, precision: 5);

                        double expectedThroughputRatio = baseThroughput > 0
                            ? expected.Throughput / baseThroughput
                            : 1.0;
                        if (double.IsNaN(expectedThroughputRatio) || double.IsInfinity(expectedThroughputRatio))
                        {
                            expectedThroughputRatio = 1.0;
                        }
                        Assert.Equal(expectedThroughputRatio, comparison.ThroughputRatio, precision: 5);

                        Assert.Equal(expected.PeakMemory - basePeak, comparison.PeakWorkingSetDeltaBytes);
                        Assert.Equal(expected.ManagedAlloc - baseAlloc, comparison.ManagedAllocatedDeltaBytes);
                    }
                }
            }
            catch (Exception ex) when (ex is not (OutOfMemoryException or StackOverflowException or
                AccessViolationException or AppDomainUnloadedException or BadImageFormatException))
            {
                exceptions.Add(ex);
            }
        });

        Assert.Empty(exceptions);
    }

    [Fact]
    public void EvidenceReportOverload_ExtractsAndCalculatesCorrectly()
    {
        var cpuReport = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "evidence-stress",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            RequestedProvider = "cpu",
            ActualProvider = "cpu",
            TimingsMilliseconds = new Dictionary<string, double?>
            {
                ["pipeline:p50"] = 100.0,
                ["pipeline:throughput"] = 10.0,
            },
            MemoryBytes = new Dictionary<string, long?>
            {
                ["peakWorkingSetBytes"] = 10L * OneTb,
                ["managedAllocatedBytes"] = 5L * OneTb,
            },
            Stages = [],
        };

        var dmlReport = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "evidence-stress",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            StartedAtUtc = DateTimeOffset.UtcNow,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            RequestedProvider = "directml",
            ActualProvider = "directml",
            TimingsMilliseconds = new Dictionary<string, double?>
            {
                ["pipeline:p50"] = 0.01, // 10 microseconds
                ["pipeline:throughput"] = 100_000.0,
            },
            MemoryBytes = new Dictionary<string, long?>
            {
                ["peakWorkingSetBytes"] = 60L * OneTb,
                ["managedAllocatedBytes"] = 2L * OneTb,
            },
            Stages = [],
        };

        var reports = new Dictionary<string, BenchmarkEvidenceReport>
        {
            ["cpu"] = cpuReport,
            ["directml"] = dmlReport,
        };

        ExecutionProviderMatrixReport matrixReport = ExecutionProviderMatrixRunner.CompareProviders(
            scenario: "evidence-stress",
            baselineProvider: "cpu",
            providerReports: reports);

        ProviderComparisonMetrics dml = matrixReport.Comparisons.First(c => c.Provider == "directml");

        // 100.0 / 0.01 = 10,000x speedup
        Assert.Equal(10000.0, dml.SpeedupFactor, precision: 4);
        // 0.01 - 100.0 = -99.99ms delta
        Assert.Equal(-99.99, dml.LatencyDeltaMilliseconds, precision: 4);
        // 100,000 / 10 = 10,000x throughput ratio
        Assert.Equal(10000.0, dml.ThroughputRatio, precision: 4);
        // 60 TB - 10 TB = +50 TB
        Assert.Equal(50L * OneTb, dml.PeakWorkingSetDeltaBytes);
        // 2 TB - 5 TB = -3 TB
        Assert.Equal(-3L * OneTb, dml.ManagedAllocatedDeltaBytes);
    }
}
