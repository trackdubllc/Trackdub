using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests.Metrics;

/// <summary>
/// Challenger verification harness for Milestone 2:
/// 1. Multi-run sample aggregation math (odd/even counts, linear interpolation, order-independence, extreme spreads).
/// 2. Presence and non-negativity of the typed process-memory envelope and per-stage GC records.
/// 3. JSON round-trip serialization/deserialization fidelity under multiple serializer options (SchemaVersion == 2).
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
    // 2. Typed Telemetry Presence
    // =========================================================================

    [Fact]
    public void TypedMemoryTelemetry_ProcessEnvelopeAndStageCollectionsArePresent()
    {
        string[] canonicalStages =
        [
            StageNames.AudioPreparation,
            StageNames.Separation,
            StageNames.Asr,
            StageNames.LipSync,
            StageNames.Tts,
        ];

        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "controlled-all-canonical",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = new BenchmarkProcessMemoryTelemetry
            {
                WorkingSetStartBytes = 120_000_000,
                WorkingSetEndBytes = 180_000_000,
                PeakWorkingSetBytes = 195_000_000,
                ManagedAllocatedBytes = 65_000_000,
                Gen0Collections = 12,
                Gen1Collections = 4,
                Gen2Collections = 1,
            },
            StageGarbageCollection = canonicalStages.Select(stage => new BenchmarkStageGarbageCollectionTelemetry
            {
                Stage = stage,
                Gen0Collections = 2,
                Gen1Collections = 1,
                Gen2Collections = 0,
            }).ToArray(),
        };

        Assert.NotNull(report.ProcessMemory);
        Assert.NotNull(report.ProcessMemory.WorkingSetStartBytes);
        Assert.NotNull(report.ProcessMemory.WorkingSetEndBytes);
        Assert.NotNull(report.ProcessMemory.PeakWorkingSetBytes);
        Assert.NotNull(report.ProcessMemory.ManagedAllocatedBytes);
        Assert.NotNull(report.ProcessMemory.Gen0Collections);
        Assert.NotNull(report.ProcessMemory.Gen1Collections);
        Assert.NotNull(report.ProcessMemory.Gen2Collections);

        foreach (string stage in canonicalStages)
        {
            BenchmarkStageGarbageCollectionTelemetry gc = Assert.Single(
                report.StageGarbageCollection, entry => entry.Stage == stage);
            Assert.NotNull(gc.Gen0Collections);
            Assert.True(gc.Gen0Collections >= 0, $"Stage metric for '{stage}' must be non-negative.");
        }
    }

    // =========================================================================
    // 3. JSON Round-Trip Serialization & Deserialization
    // =========================================================================

    [Fact]
    public void JsonRoundTrip_WithFullTelemetry_PreservesSchemaVersionAndValues()
    {
        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 2,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "json-fidelity-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = new BenchmarkProcessMemoryTelemetry
            {
                WorkingSetStartBytes = 100_000_000,
                WorkingSetEndBytes = 150_000_000,
                PeakWorkingSetBytes = 175_000_000,
                ManagedAllocatedBytes = 35_000_000,
                Gen0Collections = 7,
                Gen1Collections = 2,
                Gen2Collections = 1,
            },
            StageGarbageCollection =
            [
                new BenchmarkStageGarbageCollectionTelemetry
                {
                    Stage = "Asr",
                    Gen0Collections = 4,
                    Gen1Collections = 1,
                    Gen2Collections = 0,
                },
            ],
            Configuration = new Dictionary<string, string> { ["threads"] = "8" },
        };

        // Roundtrip with BenchmarkReportWriter.SerializerOptions
        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        BenchmarkEvidenceReport? restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(original.RunId, restored.RunId);
        Assert.Equal(original.Status, restored.Status);
        Assert.Equal(original.ProcessMemory!.PeakWorkingSetBytes, restored.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal(original.ProcessMemory.ManagedAllocatedBytes, restored.ProcessMemory.ManagedAllocatedBytes);
        Assert.Equal(original.ProcessMemory.Gen0Collections, restored.ProcessMemory.Gen0Collections);
        Assert.Equal(original.StageGarbageCollection.Count, restored.StageGarbageCollection.Count);
        Assert.Equal("Asr", Assert.Single(restored.StageGarbageCollection).Stage);
    }

    [Fact]
    public void JsonRoundTrip_WebDefaultsCamelCase_SurvivesSerializationRoundTrip()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 2,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "web-options-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = new BenchmarkProcessMemoryTelemetry
            {
                WorkingSetStartBytes = 200_000_000,
                WorkingSetEndBytes = 250_000_000,
                PeakWorkingSetBytes = 300_000_000,
                ManagedAllocatedBytes = 50_000_000,
                Gen0Collections = 10,
                Gen1Collections = 3,
                Gen2Collections = 1,
            },
            StageGarbageCollection =
            [
                new BenchmarkStageGarbageCollectionTelemetry
                {
                    Stage = "Separation",
                    Gen0Collections = 3,
                    Gen1Collections = 1,
                    Gen2Collections = 0,
                },
            ],
        };

        string json = JsonSerializer.Serialize(original, options);

        // Deserializing with web options should parse property names case-insensitively
        BenchmarkEvidenceReport? restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, options);

        Assert.NotNull(restored);
        Assert.Equal(2, restored.SchemaVersion);
        Assert.Equal(original.ProcessMemory!.PeakWorkingSetBytes, restored.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal(original.StageGarbageCollection.Count, restored.StageGarbageCollection.Count);
    }

    [Fact]
    public void JsonRoundTrip_NullValues_HandledGracefully()
    {
        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 2,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "null-values-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = new BenchmarkProcessMemoryTelemetry
            {
                WorkingSetStartBytes = 100_000_000,
                PeakWorkingSetBytes = null,
                ManagedAllocatedBytes = null,
            },
            StageGarbageCollection =
            [
                new BenchmarkStageGarbageCollectionTelemetry
                {
                    Stage = "Tts",
                    Gen0Collections = null,
                    Gen1Collections = null,
                    Gen2Collections = null,
                },
            ],
        };

        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        BenchmarkEvidenceReport? restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(restored);
        Assert.Null(restored.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Null(restored.ProcessMemory.ManagedAllocatedBytes);
        Assert.Null(Assert.Single(restored.StageGarbageCollection).Gen0Collections);
    }

    [Fact]
    public void JsonRoundTrip_CrossSerializerCompatibility_WebToReportWriterOptions()
    {
        var webOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() }
        };

        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 2,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "cross-serializer-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = new BenchmarkProcessMemoryTelemetry
            {
                WorkingSetStartBytes = 100_000_000,
                PeakWorkingSetBytes = 150_000_000,
                ManagedAllocatedBytes = 25_000_000,
            },
            StageGarbageCollection =
            [
                new BenchmarkStageGarbageCollectionTelemetry
                {
                    Stage = "Asr",
                    Gen0Collections = 3,
                },
            ],
        };

        // 1. Serialize with Web (camelCase property names)
        string webJson = JsonSerializer.Serialize(original, webOptions);

        // 2. Deserialize with Web (case-insensitive) - must succeed
        var fromWeb = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(webJson, webOptions);
        Assert.NotNull(fromWeb);
        Assert.Equal(150_000_000L, fromWeb.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Single(fromWeb.StageGarbageCollection);

        // 3. Serialize with BenchmarkReportWriter.SerializerOptions (PascalCase)
        string writerJson = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);

        // 4. Deserialize with Web (case-insensitive) - must succeed
        var fromWriterWithWeb = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(writerJson, webOptions);
        Assert.NotNull(fromWriterWithWeb);
        Assert.Equal(150_000_000L, fromWriterWithWeb.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal("Asr", Assert.Single(fromWriterWithWeb.StageGarbageCollection).Stage);
    }

    // =========================================================================
    // 4. Fallback Handling When Samples Are Missing or Empty
    // =========================================================================

    [Fact]
    public void StageGarbageCollection_FallbackToLastClockDelta_WhenStageMemorySamplesEmpty()
    {
        // Simulate the runner's stage-GC mapping: when stageMemorySamples has no entry for a
        // stage, it falls back to the stage clock's delta.
        var fallbackDelta = new ResourceTelemetryDelta(
            WorkingSetDeltaBytes: 5_000_000,
            PeakWorkingSetBytes: 80_000_000,
            ManagedAllocatedBytes: 15_000_000,
            Gen0Collections: 2,
            Gen1Collections: 1,
            Gen2Collections: 0);

        var stageMemorySamples = new Dictionary<string, List<ResourceTelemetryDelta>>(StringComparer.OrdinalIgnoreCase);
        // "FallbackStage" is NOT in stageMemorySamples

        const string stageName = "FallbackStage";
        ResourceTelemetryDelta summary =
            stageMemorySamples.TryGetValue(stageName, out var samples) && samples.Count > 0
                ? samples[0]
                : fallbackDelta;

        var entry = new BenchmarkStageGarbageCollectionTelemetry
        {
            Stage = stageName,
            Gen0Collections = summary.Gen0Collections,
            Gen1Collections = summary.Gen1Collections,
            Gen2Collections = summary.Gen2Collections,
        };

        Assert.Equal(2L, entry.Gen0Collections);
        Assert.Equal(1L, entry.Gen1Collections);
        Assert.Equal(0L, entry.Gen2Collections);
    }
}
