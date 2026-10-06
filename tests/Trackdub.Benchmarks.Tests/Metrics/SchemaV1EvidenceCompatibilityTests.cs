using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Benchmarks.Reports;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Metrics;

/// <summary>
/// Locks the schema-v2 contract's read compatibility with evidence persisted by an older build.
/// Schema v1 memory measurements remain available to export, comparison, and re-save.
/// </summary>
public sealed class SchemaV1EvidenceCompatibilityTests
{
    private static string FixturePath =>
        Path.Join(AppContext.BaseDirectory, "Fixtures", "benchmark-evidence-schema-v1.json");

    [Fact]
    public void Fixture_is_a_real_schema_v1_report_with_the_legacy_memory_map()
    {
        // Guards the fixture itself: if it is ever regenerated from the current contract this
        // fails, so the read-compatibility test below cannot silently stop exercising a v1 file.
        string json = File.ReadAllText(FixturePath);
        using var document = JsonDocument.Parse(json);

        Assert.Equal(1, document.RootElement.GetProperty("SchemaVersion").GetInt32());
        Assert.True(document.RootElement.TryGetProperty("MemoryBytes", out JsonElement memory));
        Assert.True(memory.TryGetProperty("peakWorkingSetBytes", out _));
        Assert.False(document.RootElement.TryGetProperty("ProcessMemory", out _));
        Assert.False(document.RootElement.TryGetProperty("StageGarbageCollection", out _));
    }

    [Fact]
    public async Task Schema_v1_fixture_loads_under_the_schema_v2_contractAsync()
    {
        BenchmarkEvidenceReport? report = await BenchmarkReportExporter.LoadEvidenceJsonAsync(FixturePath);

        Assert.NotNull(report);

        // The version marker survives verbatim so a reader can still tell the file's age.
        Assert.Equal(1, report.SchemaVersion);

        // Recognized legacy measurements populate the typed fields; the entire map is retained.
        Assert.Equal(167772160L, report.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal(44040192L, report.ProcessMemory.ManagedAllocatedBytes);
        Assert.Equal(10485760L, report.LegacyMemoryBytes!["stage:Asr:allocatedBytes"]);
        Assert.Equal(3L, Assert.Single(report.StageGarbageCollection).Gen0Collections);

        // Everything the typed contract still models round-trips from the v1 file.
        Assert.Equal(BenchmarkEvidenceKind.Benchmark, report.Kind);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Equal("asr", report.Scenario);
        Assert.Equal("fresh-process", report.RunMode);
        Assert.Equal(240.5, report.TimingsMilliseconds["pipeline"]);
        Assert.Equal("onnx-community/whisper-tiny", report.ActualModel);
        Assert.Equal("cpu", report.ActualProvider);
        Assert.Equal("3", report.Configuration["runCount"]);
        Assert.Equal("1.20.0", report.RuntimeVersions["onnxruntime"]);
        Assert.Equal(10_485_760L, report.Counters["phase:decode:allocatedBytes"]);
        Assert.Equal(95.0, report.ResourceTelemetryBounds?.MaxCpuPercent);
        Assert.Equal(ResourceTelemetryStatus.Passed, report.ResourceValidationStatus);

        BenchmarkEvidenceStage stage = Assert.Single(report.Stages);
        Assert.Equal("Asr", stage.Name);
        Assert.Equal(BenchmarkEvidenceStatus.Completed, stage.Status);
        Assert.Equal(240.5, stage.DurationMilliseconds);

        BenchmarkStageResourceTelemetry sample = Assert.Single(report.ResourceTelemetry);
        Assert.Equal("Asr", sample.Stage);
        Assert.Equal("measured", sample.Phase);
        Assert.Equal(ResourceTelemetryStatus.Passed, sample.Validation.Status);
        ResourceTelemetryCheck workingSet = Assert.Single(
            sample.Validation.Checks, check => check.Metric == "workingSetBytes");
        Assert.Equal(157_286_400d, workingSet.ObservedValue);

        ResourceTelemetryDistribution distribution = Assert.Single(report.ResourceDistribution);
        Assert.Equal("Asr", distribution.Stage);
        Assert.Equal("workingSetBytes", Assert.Single(distribution.Metrics).Metric);
    }

    [Fact]
    public async Task Schema_v1_fixture_also_reads_under_web_defaultsAsync()
    {
        // The repository reads with JsonSerializerDefaults.Web (camelCase, case-insensitive) while
        // the fixture is PascalCase, which is what BenchmarkReportWriter emits. This proves the
        // persistence read path tolerates the same v1 file byte-for-byte.
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
            Converters = { new JsonStringEnumConverter() },
        };

        await using FileStream stream = File.OpenRead(FixturePath);
        BenchmarkEvidenceReport? report = await JsonSerializer.DeserializeAsync<BenchmarkEvidenceReport>(stream, options);

        Assert.NotNull(report);
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(167772160L, report.ProcessMemory!.PeakWorkingSetBytes);
        Assert.Equal(44040192L, report.ProcessMemory.ManagedAllocatedBytes);
        Assert.Equal(10485760L, report.LegacyMemoryBytes!["stage:Asr:allocatedBytes"]);
        Assert.Equal(3L, Assert.Single(report.StageGarbageCollection).Gen0Collections);
        Assert.Equal(240.5, report.TimingsMilliseconds["pipeline"]);
    }
    [Fact]
    public async Task Legacy_memory_survives_export_comparison_and_round_trip()
    {
        BenchmarkEvidenceReport report = (await BenchmarkReportExporter.LoadEvidenceJsonAsync(FixturePath))!;
        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(report);
        Assert.Contains("Peak working set", markdown);
        Assert.DoesNotContain("| Peak working set | — |", markdown);
        var newer = report with
        {
            ProcessMemory = report.ProcessMemory! with { PeakWorkingSetBytes = 200000000L },
        };
        var compared = ExecutionProviderMatrixRunner.CompareProviders("asr", "cpu",
            new Dictionary<string, BenchmarkEvidenceReport> { ["cpu"] = report, ["other"] = newer });
        Assert.Equal(32227840L, compared.Comparisons.Single(x => x.Provider == "other").PeakWorkingSetDeltaBytes);
        var restored = JsonSerializer.Deserialize<BenchmarkEvidenceReport>(JsonSerializer.Serialize(report))!;
        Assert.Equal(report.ProcessMemory, restored.ProcessMemory);
        Assert.Equal(report.LegacyMemoryBytes!.OrderBy(x => x.Key), restored.LegacyMemoryBytes!.OrderBy(x => x.Key));
        Assert.Equal(report.StageGarbageCollection.Single(), restored.StageGarbageCollection.Single());
    }

    [Theory]
    [InlineData(null, 100L, 200L, 200L)]
    [InlineData(null, 100L, null, 100L)]
    [InlineData(null, null, 200L, 200L)]
    [InlineData(null, null, null, null)]
    [InlineData(300L, 100L, 200L, 300L)]
    public void Process_peak_uses_endpoints_only_when_sampling_is_unavailable(long? sampled, long? start, long? end, long? expected)
    {
        Assert.Equal(expected, ControlledDubbingBenchmarkRunner.ResolveProcessPeak(sampled, start, end));
    }
}
