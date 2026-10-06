using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

public sealed class BenchmarkMemoryEvidenceReportTests
{
    [Fact]
    public void ProcessMemory_CarriesTheRunLevelEnvelope()
    {
        var processMemory = new BenchmarkProcessMemoryTelemetry
        {
            WorkingSetStartBytes = 100_000_000,
            WorkingSetEndBytes = 145_000_000,
            PeakWorkingSetBytes = 160_000_000,
            ManagedAllocatedBytes = 42_000_000,
            Gen0Collections = 6,
            Gen1Collections = 2,
            Gen2Collections = 1,
        };

        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "controlled-memory-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = processMemory,
        };

        Assert.NotNull(report.ProcessMemory);
        Assert.Equal(100_000_000L, report.ProcessMemory.WorkingSetStartBytes);
        Assert.Equal(145_000_000L, report.ProcessMemory.WorkingSetEndBytes);
        Assert.Equal(160_000_000L, report.ProcessMemory.PeakWorkingSetBytes);
        Assert.Equal(42_000_000L, report.ProcessMemory.ManagedAllocatedBytes);
        Assert.Equal(6L, report.ProcessMemory.Gen0Collections);
        Assert.Equal(2L, report.ProcessMemory.Gen1Collections);
        Assert.Equal(1L, report.ProcessMemory.Gen2Collections);
    }

    [Fact]
    public void StageGarbageCollection_CarriesPerStageDeltas()
    {
        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "stage-telemetry-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
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
        };

        BenchmarkStageGarbageCollectionTelemetry entry = Assert.Single(report.StageGarbageCollection);
        Assert.Equal("Asr", entry.Stage);
        Assert.Equal(4L, entry.Gen0Collections);
        Assert.Equal(1L, entry.Gen1Collections);
        Assert.Equal(0L, entry.Gen2Collections);
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

        // Aggregation logic matching ControlledDubbingBenchmarkRunner's median/peak reduction.
        var sortedAlloc = samples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
        long medianAllocated = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
        long peakWs = samples.Max(s => s.PeakWorkingSetBytes);
        int gen0 = (int)Math.Round(PercentileCalculator.CalculatePercentile(samples.Select(s => (double)s.Gen0Collections).OrderBy(x => x).ToArray(), 0.5));

        Assert.Equal(30_000_000L, medianAllocated);
        Assert.Equal(165_000_000L, peakWs);
        Assert.Equal(4, gen0);
    }

    [Fact]
    public void EvidenceReport_SerializationRoundTrip_PreservesTypedMemoryTelemetry()
    {
        var original = new BenchmarkEvidenceReport
        {
            SchemaVersion = 2,
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "json-roundtrip-test",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ProcessMemory = new BenchmarkProcessMemoryTelemetry
            {
                WorkingSetStartBytes = 100_000_000,
                WorkingSetEndBytes = 150_000_000,
                PeakWorkingSetBytes = 180_000_000,
                ManagedAllocatedBytes = 35_000_000,
                Gen0Collections = 5,
                Gen1Collections = 2,
                Gen2Collections = 1,
            },
            StageGarbageCollection =
            [
                new BenchmarkStageGarbageCollectionTelemetry
                {
                    Stage = "Asr",
                    Gen0Collections = 3,
                    Gen1Collections = 1,
                    Gen2Collections = 0,
                },
            ],
        };

        string json = JsonSerializer.Serialize(original, BenchmarkReportWriter.SerializerOptions);
        BenchmarkEvidenceReport? deserialized = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(json, BenchmarkReportWriter.SerializerOptions);

        Assert.NotNull(deserialized);
        Assert.Equal(2, deserialized.SchemaVersion);
        Assert.Equal(original.ProcessMemory!.PeakWorkingSetBytes, deserialized.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal(original.ProcessMemory, deserialized.ProcessMemory);

        Assert.Equal(original.StageGarbageCollection.Count, deserialized.StageGarbageCollection.Count);
        BenchmarkStageGarbageCollectionTelemetry restored = Assert.Single(deserialized.StageGarbageCollection);
        Assert.Equal("Asr", restored.Stage);
        Assert.Equal(original.StageGarbageCollection.Single(), restored);
    }
}
