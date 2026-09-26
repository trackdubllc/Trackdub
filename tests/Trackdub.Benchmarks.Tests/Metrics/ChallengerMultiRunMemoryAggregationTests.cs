using System.Text.Json;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests.Metrics;

/// <summary>
/// Empirical challenger verification harness for Milestone 2:
/// 1. Multi-run sample aggregation math (odd/even counts, linear interpolation, order-independence, extreme spreads).
/// 2. Verification of all canonical process keys and stage keys in MemoryBytes and TimingsMilliseconds.
/// 3. JSON round-trip serialization/deserialization fidelity under multiple serializer options (SchemaVersion == 1).
/// 4. Fallback behavior when stage samples are empty or unrecorded.
/// </summary>
public sealed class ChallengerMultiRunMemoryAggregationTests
{
    // =========================================================================
    // 1. Multi-Run Sample Aggregation Mathematics
    // =========================================================================

    [Fact]
    public void MultiRunAggregation_SingleRun_ReturnsExactSampleValues()
    {
        var samples = new List<ResourceTelemetryDelta>
        {
            new(WorkingSetDeltaBytes: 15_000_000, PeakWorkingSetBytes: 120_000_000, ManagedAllocatedBytes: 45_000_000, Gen0Collections: 6, Gen1Collections: 2, Gen2Collections: 1)
        };

        var sortedAlloc = samples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
        long allocated = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
        long peakWs = samples.Max(s => s.PeakWorkingSetBytes);
        int gen0 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen0Collections).OrderBy(x => x).ToArray(), 0.5));
        int gen1 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen1Collections).OrderBy(x => x).ToArray(), 0.5));
        int gen2 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen2Collections).OrderBy(x => x).ToArray(), 0.5));

        Assert.Equal(45_000_000L, allocated);
        Assert.Equal(120_000_000L, peakWs);
        Assert.Equal(6, gen0);
        Assert.Equal(2, gen1);
        Assert.Equal(1, gen2);
    }

    [Theory]
    [InlineData(new long[] { 10_000_000, 20_000_000 }, 15_000_000L, 20_000_000L)] // 2 runs (even) -> average
    [InlineData(new long[] { 20_000_000, 10_000_000 }, 15_000_000L, 20_000_000L)] // 2 runs reversed -> same result
    [InlineData(new long[] { 10_000_000, 50_000_000, 30_000_000 }, 30_000_000L, 50_000_000L)] // 3 runs (odd, unsorted) -> middle 30M, peak 50M
    [InlineData(new long[] { 10_000_000, 20_000_000, 30_000_000, 40_000_000 }, 25_000_000L, 40_000_000L)] // 4 runs (even) -> (20M + 30M)/2 = 25M
    [InlineData(new long[] { 5_000_000, 90_000_000, 15_000_000, 30_000_000, 20_000_000 }, 20_000_000L, 90_000_000L)] // 5 runs (odd, unsorted) -> sorted: 5, 15, 20, 30, 90 -> median 20M, peak 90M
    public void MultiRunAggregation_OddAndEvenCounts_ComputesAccurateMedianAndMaxPeak(
        long[] sampleAllocations,
        long expectedMedianAllocated,
        long expectedPeakWs)
    {
        var samples = sampleAllocations.Select((alloc, idx) => new ResourceTelemetryDelta(
            WorkingSetDeltaBytes: alloc / 2,
            PeakWorkingSetBytes: alloc,
            ManagedAllocatedBytes: alloc,
            Gen0Collections: idx + 1,
            Gen1Collections: idx / 2,
            Gen2Collections: 0)).ToList();

        var sortedAlloc = samples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
        long allocated = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
        long peakWs = samples.Max(s => s.PeakWorkingSetBytes);

        Assert.Equal(expectedMedianAllocated, allocated);
        Assert.Equal(expectedPeakWs, peakWs);
    }

    [Fact]
    public void MultiRunAggregation_OrderIndependence_ShuffledRunsProduceIdenticalAggregates()
    {
        var deltas = new List<ResourceTelemetryDelta>
        {
            new(10_000_000, 100_000_000, 20_000_000, 4, 1, 0),
            new(15_000_000, 150_000_000, 35_000_000, 7, 3, 1),
            new(12_000_000, 120_000_000, 28_000_000, 5, 2, 0),
            new(8_000_000,  180_000_000, 22_000_000, 3, 1, 0),
            new(20_000_000, 200_000_000, 50_000_000, 10, 4, 2),
            new(11_000_000, 110_000_000, 25_000_000, 4, 2, 1),
            new(13_000_000, 130_000_000, 30_000_000, 6, 2, 0),
        };

        (long alloc, long peak, int g0, int g1, int g2) ComputeAggregates(List<ResourceTelemetryDelta> list)
        {
            var sorted = list.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
            long a = (long)Math.Round(PercentileCalculator.CalculatePercentile(sorted, 0.5));
            long p = list.Max(s => s.PeakWorkingSetBytes);
            int gen0 = (int)Math.Round(PercentileCalculator.CalculatePercentile(list.Select(s => (double)s.Gen0Collections).OrderBy(x => x).ToArray(), 0.5));
            int gen1 = (int)Math.Round(PercentileCalculator.CalculatePercentile(list.Select(s => (double)s.Gen1Collections).OrderBy(x => x).ToArray(), 0.5));
            int gen2 = (int)Math.Round(PercentileCalculator.CalculatePercentile(list.Select(s => (double)s.Gen2Collections).OrderBy(x => x).ToArray(), 0.5));
            return (a, p, gen0, gen1, gen2);
        }

        var baseline = ComputeAggregates(deltas);

        // Test multiple random permutations
        var rng = new Random(42);
        for (int i = 0; i < 10; i++)
        {
            var shuffled = deltas.OrderBy(_ => rng.Next()).ToList();
            var result = ComputeAggregates(shuffled);

            Assert.Equal(baseline.alloc, result.alloc);
            Assert.Equal(baseline.peak, result.peak);
            Assert.Equal(baseline.g0, result.g0);
            Assert.Equal(baseline.g1, result.g1);
            Assert.Equal(baseline.g2, result.g2);
        }
    }

    [Fact]
    public void MultiRunAggregation_ExtremeValueSpread_PreservesPrecisionWithoutDoubleTruncation()
    {
        // 100 KB vs 10 GB vs 20 GB
        long small = 100 * 1024L;
        long medium = 10L * 1024 * 1024 * 1024;
        long large = 20L * 1024 * 1024 * 1024;

        var samples = new List<ResourceTelemetryDelta>
        {
            new(small, small, small, 1, 0, 0),
            new(large, large, large, 100, 20, 5),
            new(medium, medium, medium, 50, 10, 2),
        };

        var sorted = samples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
        long allocated = (long)Math.Round(PercentileCalculator.CalculatePercentile(sorted, 0.5));
        long peak = samples.Max(s => s.PeakWorkingSetBytes);

        Assert.Equal(medium, allocated);
        Assert.Equal(large, peak);
    }

    // =========================================================================
    // 2. Canonical Keys Verification
    // =========================================================================

    [Fact]
    public void CanonicalKeys_AllProcessAndStageKeysPresentAndNonNegative()
    {
        string[] canonicalStages =
        [
            StageNames.AudioPreparation,
            StageNames.Separation,
            StageNames.Asr,
            StageNames.LipSync,
            StageNames.Tts,
        ];

        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 120_000_000,
            ["processWorkingSetEnd"] = 180_000_000,
            ["processPeakWorkingSet"] = 195_000_000,
            ["peakWorkingSetBytes"] = 195_000_000,
            ["managedAllocatedBytes"] = 65_000_000,
            ["gen0Collections"] = 12,
            ["gen1Collections"] = 4,
            ["gen2Collections"] = 1,
            ["gpuDedicatedBytes"] = null,
        };

        foreach (string stage in canonicalStages)
        {
            memory[$"stage:{stage}:allocatedBytes"] = 10_000_000;
            memory[$"stage:{stage}:peakWorkingSet"] = 150_000_000;
            memory[$"stage:{stage}:gen0"] = 2;
            memory[$"stage:{stage}:gen1"] = 1;
            memory[$"stage:{stage}:gen2"] = 0;
        }

        var timings = new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["total"] = 1500.0,
            ["pipeline"] = 1200.0,
            ["hostCreation"] = 150.0,
            ["fixturePreparation"] = 50.0,
            ["prerequisites"] = 100.0,
        };

        foreach (string stage in canonicalStages)
        {
            timings[$"stage:{stage}:p50"] = 240.0;
            timings[$"stage:{stage}:min"] = 200.0;
            timings[$"stage:{stage}:max"] = 280.0;
            timings[$"stage:{stage}:mean"] = 240.0;
            timings[$"stage:{stage}:p90"] = 270.0;
            timings[$"stage:{stage}:p99"] = 279.0;
            timings[$"stage:{stage}:throughput"] = 10.0;
            timings[$"stage:{stage}:sampleCount"] = 3.0;
        }

        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "controlled-all-canonical",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
            TimingsMilliseconds = timings,
        };

        // Assert 8 required process keys
        string[] requiredProcessKeys =
        [
            "processWorkingSetStart",
            "processWorkingSetEnd",
            "processPeakWorkingSet",
            "peakWorkingSetBytes",
            "managedAllocatedBytes",
            "gen0Collections",
            "gen1Collections",
            "gen2Collections",
        ];

        foreach (string key in requiredProcessKeys)
        {
            Assert.True(report.MemoryBytes.ContainsKey(key), $"Missing required process key: {key}");
            Assert.NotNull(report.MemoryBytes[key]);
            Assert.True(report.MemoryBytes[key] >= 0, $"Process metric '{key}' must be non-negative.");
        }

        // Assert 5 stage keys per canonical stage
        string[] requiredStageMetrics = ["allocatedBytes", "peakWorkingSet", "gen0", "gen1", "gen2"];
        foreach (string stage in canonicalStages)
        {
            foreach (string key in requiredStageMetrics.Select(metric => $"stage:{stage}:{metric}"))
            {
                Assert.True(report.MemoryBytes.ContainsKey(key), $"Missing required stage key: {key}");
                Assert.NotNull(report.MemoryBytes[key]);
                Assert.True(report.MemoryBytes[key] >= 0, $"Stage metric '{key}' must be non-negative.");
            }
        }
    }

    // =========================================================================
    // 3. JSON Round-Trip Serialization & Deserialization
    // =========================================================================

    [Fact]
    public void JsonRoundTrip_WithFullTelemetry_PreservesSchemaVersionAndValues()
    {
        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 100_000_000,
            ["processWorkingSetEnd"] = 150_000_000,
            ["processPeakWorkingSet"] = 175_000_000,
            ["peakWorkingSetBytes"] = 175_000_000,
            ["managedAllocatedBytes"] = 35_000_000,
            ["gen0Collections"] = 7,
            ["gen1Collections"] = 2,
            ["gen2Collections"] = 1,
            ["gpuDedicatedBytes"] = null,
            ["stage:Asr:allocatedBytes"] = 20_000_000,
            ["stage:Asr:peakWorkingSet"] = 160_000_000,
            ["stage:Asr:gen0"] = 4,
            ["stage:Asr:gen1"] = 1,
            ["stage:Asr:gen2"] = 0,
        };

        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "json-fidelity-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
            Configuration = new Dictionary<string, string> { ["threads"] = "8" },
        };

        // Roundtrip with BenchmarkReportWriter.SerializerOptions
        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        BenchmarkEvidenceReport? restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal(original.RunId, restored.RunId);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.MemoryBytes.Count, restored.MemoryBytes.Count);

        foreach ((string key, long? expectedVal) in original.MemoryBytes)
        {
            Assert.True(restored.MemoryBytes.ContainsKey(key), $"Deserialized report missing key: {key}");
            Assert.Equal(expectedVal, restored.MemoryBytes[key]);
        }
    }

    [Fact]
    public void JsonRoundTrip_WebDefaultsCamelCase_SurvivesSerializationRoundTrip()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 200_000_000,
            ["processWorkingSetEnd"] = 250_000_000,
            ["processPeakWorkingSet"] = 300_000_000,
            ["peakWorkingSetBytes"] = 300_000_000,
            ["managedAllocatedBytes"] = 50_000_000,
            ["gen0Collections"] = 10,
            ["gen1Collections"] = 3,
            ["gen2Collections"] = 1,
            ["stage:Separation:allocatedBytes"] = 15_000_000,
            ["stage:Separation:peakWorkingSet"] = 280_000_000,
            ["stage:Separation:gen0"] = 3,
            ["stage:Separation:gen1"] = 1,
            ["stage:Separation:gen2"] = 0,
        };

        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "web-options-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
        };

        string json = JsonSerializer.Serialize(original, options);

        // Deserializing with web options should parse property names case-insensitively
        BenchmarkEvidenceReport? restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(1, restored.SchemaVersion);
        Assert.Equal(original.MemoryBytes.Count, restored.MemoryBytes.Count);

        foreach ((string key, long? expectedVal) in original.MemoryBytes)
        {
            Assert.True(restored.MemoryBytes.ContainsKey(key), $"Web deserialized report missing key: {key}");
            Assert.Equal(expectedVal, restored.MemoryBytes[key]);
        }
    }

    [Fact]
    public void JsonRoundTrip_NullAndGpuDedicatedBytes_HandledGracefully()
    {
        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 100_000_000,
            ["gpuDedicatedBytes"] = null,
            ["stage:Tts:allocatedBytes"] = null,
        };

        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "null-values-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
        };

        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        BenchmarkEvidenceReport? restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Null(restored.MemoryBytes["gpuDedicatedBytes"]);
        Assert.Null(restored.MemoryBytes["stage:Tts:allocatedBytes"]);
    }

    [Fact]
    public void JsonRoundTrip_CrossSerializerCompatibility_WebToReportWriterOptions()
    {
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 100_000_000,
            ["peakWorkingSetBytes"] = 150_000_000,
            ["managedAllocatedBytes"] = 25_000_000,
            ["stage:Asr:allocatedBytes"] = 12_000_000,
        };

        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 1,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "cross-serializer-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
        };

        // 1. Serialize with Web (camelCase property names)
        string webJson = JsonSerializer.Serialize(original, webOptions);

        // 2. Deserialize with Web (case-insensitive) - must succeed
        var fromWeb = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(webJson, webOptions);
        Assert.NotNull(fromWeb);
        Assert.Equal(4, fromWeb.MemoryBytes.Count);

        // 3. Serialize with BenchmarkReportWriter.SerializerOptions (PascalCase)
        string writerJson = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);

        // 4. Deserialize with Web (case-insensitive) - must succeed
        var fromWriterWithWeb = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(writerJson, webOptions);
        Assert.NotNull(fromWriterWithWeb);
        Assert.Equal(4, fromWriterWithWeb.MemoryBytes.Count);
        Assert.Equal(12_000_000L, fromWriterWithWeb.MemoryBytes["stage:Asr:allocatedBytes"]);
    }

    // =========================================================================
    // 4. Fallback Handling When Samples Are Missing or Empty
    // =========================================================================

    [Fact]
    public void StageMemoryAggregation_FallbackToLastClockDelta_WhenStageMemorySamplesEmpty()
    {
        // Simulate ControlledDubbingBenchmarkRunner lines 359-366:
        // when stageMemorySamples doesn't have entries for a stage, it falls back to lastClock.GetMemoryDelta(stageName)
        var fallbackDelta = new ResourceTelemetryDelta(
            WorkingSetDeltaBytes: 5_000_000,
            PeakWorkingSetBytes: 80_000_000,
            ManagedAllocatedBytes: 15_000_000,
            Gen0Collections: 2,
            Gen1Collections: 1,
            Gen2Collections: 0);

        var stageMemorySamples = new Dictionary<string, List<ResourceTelemetryDelta>>(StringComparer.OrdinalIgnoreCase);
        // "FallbackStage" is NOT in stageMemorySamples

        var memory = new Dictionary<string, long?>(StringComparer.Ordinal);

        string stageName = "FallbackStage";
        if (stageMemorySamples.TryGetValue(stageName, out var mSamples) && mSamples.Count > 0)
        {
            var sortedAlloc = mSamples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
            memory[$"stage:{stageName}:allocatedBytes"] = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
            memory[$"stage:{stageName}:peakWorkingSet"] = mSamples.Max(s => s.PeakWorkingSetBytes);
        }
        else
        {
            // Runner fallback logic
            memory[$"stage:{stageName}:allocatedBytes"] = fallbackDelta.ManagedAllocatedBytes;
            memory[$"stage:{stageName}:peakWorkingSet"] = fallbackDelta.PeakWorkingSetBytes;
            memory[$"stage:{stageName}:gen0"] = fallbackDelta.Gen0Collections;
            memory[$"stage:{stageName}:gen1"] = fallbackDelta.Gen1Collections;
            memory[$"stage:{stageName}:gen2"] = fallbackDelta.Gen2Collections;
        }

        Assert.Equal(15_000_000L, memory[$"stage:{stageName}:allocatedBytes"]);
        Assert.Equal(80_000_000L, memory[$"stage:{stageName}:peakWorkingSet"]);
        Assert.Equal(2, memory[$"stage:{stageName}:gen0"]);
        Assert.Equal(1, memory[$"stage:{stageName}:gen1"]);
        Assert.Equal(0, memory[$"stage:{stageName}:gen2"]);
    }
}

