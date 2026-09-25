using System.Text.Json;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class BenchmarkMemoryEvidenceReportTests
{
    [Fact]
    public void MemoryBytes_ContainsAllRequiredProcessKeys()
    {
        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 100_000_000,
            ["processWorkingSetEnd"] = 145_000_000,
            ["processPeakWorkingSet"] = 160_000_000,
            ["peakWorkingSetBytes"] = 160_000_000, // alias for E2E Tier 1 compatibility
            ["managedAllocatedBytes"] = 42_000_000,
            ["gen0Collections"] = 6,
            ["gen1Collections"] = 2,
            ["gen2Collections"] = 1,
        };

        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "controlled-memory-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
        };

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
            Assert.True(report.MemoryBytes.ContainsKey(key), $"Report MemoryBytes must contain key '{key}'.");
            Assert.NotNull(report.MemoryBytes[key]);
            Assert.True(report.MemoryBytes[key] >= 0, $"Value for key '{key}' must be non-negative.");
        }

        Assert.Equal(report.MemoryBytes["processPeakWorkingSet"], report.MemoryBytes["peakWorkingSetBytes"]);
    }

    [Fact]
    public void MemoryBytes_ContainsAllCanonicalStageTelemetryKeys()
    {
        string[] canonicalStages =
        [
            StageNames.AudioPreparation,
            StageNames.Separation,
            StageNames.Asr,
            StageNames.LipSync,
            StageNames.Tts,
        ];

        var memory = new Dictionary<string, long?>(StringComparer.Ordinal);

        foreach (string stage in canonicalStages)
        {
            memory[$"stage:{stage}:allocatedBytes"] = 15_000_000L;
            memory[$"stage:{stage}:peakWorkingSet"] = 120_000_000L;
            memory[$"stage:{stage}:gen0"] = 4L;
            memory[$"stage:{stage}:gen1"] = 1L;
            memory[$"stage:{stage}:gen2"] = 0L;
        }

        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "stage-telemetry-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
        };

        string[] stageMetrics = ["allocatedBytes", "peakWorkingSet", "gen0", "gen1", "gen2"];

        foreach (string stage in canonicalStages)
        {
            foreach (string metric in stageMetrics)
            {
                string key = $"stage:{stage}:{metric}";
                Assert.True(report.MemoryBytes.ContainsKey(key), $"Report MemoryBytes missing stage key '{key}'.");
                Assert.NotNull(report.MemoryBytes[key]);
                Assert.True(report.MemoryBytes[key] >= 0, $"Stage metric '{key}' should be non-negative.");
            }
        }
    }

    [Fact]
    public void MultiRunStageMemoryAggregation_ComputesMedianAndPeakCorrectly()
    {
        // Simulate 3 runs of memory deltas for ASR stage
        var samples = new List<ResourceTelemetryDelta>
        {
            new(WorkingSetDeltaBytes: 10_000_000, PeakWorkingSetBytes: 150_000_000, ManagedAllocatedBytes: 30_000_000, Gen0Collections: 4, Gen1Collections: 1, Gen2Collections: 0),
            new(WorkingSetDeltaBytes: 12_000_000, PeakWorkingSetBytes: 165_000_000, ManagedAllocatedBytes: 35_000_000, Gen0Collections: 5, Gen1Collections: 2, Gen2Collections: 0),
            new(WorkingSetDeltaBytes: 8_000_000, PeakWorkingSetBytes: 140_000_000, ManagedAllocatedBytes: 28_000_000, Gen0Collections: 3, Gen1Collections: 1, Gen2Collections: 0),
        };

        // Aggregation logic matching ControlledDubbingBenchmarkRunner M2 implementation
        var sortedAlloc = samples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
        long medianAllocated = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
        long peakWs = samples.Max(s => s.PeakWorkingSetBytes);
        int gen0 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen0Collections).OrderBy(x => x).ToArray(), 0.5));

        Assert.Equal(30_000_000L, medianAllocated);
        Assert.Equal(165_000_000L, peakWs);
        Assert.Equal(4, gen0);
    }

    [Fact]
    public void MemoryBytes_SerializationRoundTrip_PreservesAllTelemetryKeys()
    {
        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = 100_000_000,
            ["processWorkingSetEnd"] = 150_000_000,
            ["processPeakWorkingSet"] = 180_000_000,
            ["peakWorkingSetBytes"] = 180_000_000,
            ["managedAllocatedBytes"] = 35_000_000,
            ["gen0Collections"] = 5,
            ["gen1Collections"] = 2,
            ["gen2Collections"] = 1,
            ["stage:Asr:allocatedBytes"] = 25_000_000,
            ["stage:Asr:peakWorkingSet"] = 170_000_000,
            ["stage:Asr:gen0"] = 3,
            ["stage:Asr:gen1"] = 1,
            ["stage:Asr:gen2"] = 0,
        };

        var original = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "json-roundtrip-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            MemoryBytes = memory,
        };

        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        BenchmarkEvidenceReport? deserialized = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(original.MemoryBytes.Count, deserialized.MemoryBytes.Count);

        foreach ((string key, long? value) in original.MemoryBytes)
        {
            Assert.True(deserialized.MemoryBytes.ContainsKey(key), $"Deserialized report missing key '{key}'.");
            Assert.Equal(value, deserialized.MemoryBytes[key]);
        }
    }
}
