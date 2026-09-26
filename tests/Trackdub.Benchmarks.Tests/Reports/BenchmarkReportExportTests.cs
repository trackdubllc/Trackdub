using System.Text.Json;
using Trackdub.Benchmarks.Reports;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Reports;

public sealed class BenchmarkReportExportTests : IDisposable
{
    private readonly string _tempDir;

    public BenchmarkReportExportTests()
    {
        _tempDir = Path.Join(Path.GetTempPath(), "trackdub-export-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (DirectoryNotFoundException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
        catch (IOException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
        catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
    }

    // ─── Helpers ───────────────────────────────────────────────────────

    private static BenchmarkEvidenceReport CreateSampleEvidenceReport() => new()
    {
        RunId = Guid.NewGuid(),
        Kind = BenchmarkEvidenceKind.Benchmark,
        Scenario = "full-pipeline",
        RunMode = "fresh-process",
        Status = BenchmarkEvidenceStatus.Completed,
        CompletedAtUtc = DateTimeOffset.UtcNow,
        StartedAtUtc = DateTimeOffset.UtcNow.AddSeconds(-5),
        FixtureSha256 = new string('a', 64),
        RequestedProvider = "cpu",
        ActualProvider = "cpu",
        TimingsMilliseconds = new Dictionary<string, double?>
        {
            ["pipeline"] = 1234.56,
            ["pipeline:p50"] = 1200.0,
            ["pipeline:p90"] = 1400.0,
            ["pipeline:p99"] = 1500.0,
            ["pipeline:throughput"] = 0.81,
        },
        MemoryBytes = new Dictionary<string, long?>
        {
            ["peakWorkingSetBytes"] = 256 * 1024 * 1024L,
            ["managedAllocatedBytes"] = 64 * 1024 * 1024L,
            ["gen0Collections"] = 12,
            ["gen1Collections"] = 3,
            ["gen2Collections"] = 1,
        },
        Stages =
        [
            new BenchmarkEvidenceStage
            {
                Name = "audio-prep",
                Status = BenchmarkEvidenceStatus.Completed,
                DurationMilliseconds = 100.5,
                ActualProvider = "cpu",
            },
            new BenchmarkEvidenceStage
            {
                Name = "separation",
                Status = BenchmarkEvidenceStatus.Completed,
                DurationMilliseconds = 350.2,
                ActualProvider = "cpu",
            },
        ],
    };

    private static ExecutionProviderMatrixReport CreateSampleMatrixReport() =>
        ExecutionProviderMatrixRunner.CompareProviders(
            "full-pipeline",
            "cpu",
            new Dictionary<string, (double P50, double Throughput, long PeakMemory, long ManagedAlloc)>
            {
                ["cpu"] = (100.0, 10.0, 256 * 1024 * 1024L, 64 * 1024 * 1024L),
                ["directml"] = (50.0, 20.0, 512 * 1024 * 1024L, 80 * 1024 * 1024L),
                ["tensorrt"] = (25.0, 40.0, 768 * 1024 * 1024L, 96 * 1024 * 1024L),
            },
            DateTimeOffset.Parse("2026-01-15T12:00:00Z"));

    // ─── Evidence JSON Export ──────────────────────────────────────────

    [Fact]
    public async Task ExportJsonAsync_EvidenceReport_CreatesValidJsonFile()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();
        string outputPath = Path.Join(_tempDir, "evidence.json");

        await BenchmarkReportExporter.ExportJsonAsync(report, outputPath);

        Assert.True(File.Exists(outputPath));
        string json = await File.ReadAllTextAsync(outputPath);
        Assert.False(string.IsNullOrWhiteSpace(json));

        // Verify it deserializes without error
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("full-pipeline", doc.RootElement.GetProperty("Scenario").GetString());
        Assert.Equal("Completed", doc.RootElement.GetProperty("Status").GetString());
    }

    [Fact]
    public async Task ExportJsonAsync_EvidenceReport_RoundTripsCorrectly()
    {
        BenchmarkEvidenceReport original = CreateSampleEvidenceReport();
        string outputPath = Path.Join(_tempDir, "evidence-rt.json");

        await BenchmarkReportExporter.ExportJsonAsync(original, outputPath);
        BenchmarkEvidenceReport? loaded = await BenchmarkReportExporter.LoadEvidenceJsonAsync(outputPath);

        Assert.NotNull(loaded);
        Assert.Equal(original.RunId, loaded.RunId);
        Assert.Equal(original.Scenario, loaded.Scenario);
        Assert.Equal(original.Status, loaded.Status);
        Assert.Equal(original.RunMode, loaded.RunMode);
        Assert.Equal(original.TimingsMilliseconds["pipeline"], loaded.TimingsMilliseconds["pipeline"]);
        Assert.Equal(original.MemoryBytes["peakWorkingSetBytes"], loaded.MemoryBytes["peakWorkingSetBytes"]);
        Assert.Equal(original.Stages.Count, loaded.Stages.Count);
    }

    // ─── Matrix JSON Export ────────────────────────────────────────────

    [Fact]
    public async Task ExportJsonAsync_MatrixReport_CreatesValidJsonFile()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();
        string outputPath = Path.Join(_tempDir, "matrix.json");

        await BenchmarkReportExporter.ExportJsonAsync(report, outputPath);

        Assert.True(File.Exists(outputPath));
        string json = await File.ReadAllTextAsync(outputPath);
        using var doc = JsonDocument.Parse(json);
        Assert.Equal("full-pipeline", doc.RootElement.GetProperty("Scenario").GetString());
        Assert.Equal(3, doc.RootElement.GetProperty("Comparisons").GetArrayLength());
    }

    [Fact]
    public async Task ExportJsonAsync_MatrixReport_RoundTripsCorrectly()
    {
        ExecutionProviderMatrixReport original = CreateSampleMatrixReport();
        string outputPath = Path.Join(_tempDir, "matrix-rt.json");

        await BenchmarkReportExporter.ExportJsonAsync(original, outputPath);
        ExecutionProviderMatrixReport? loaded = await BenchmarkReportExporter.LoadMatrixJsonAsync(outputPath);

        Assert.NotNull(loaded);
        Assert.Equal(original.Scenario, loaded.Scenario);
        Assert.Equal(original.BaselineProvider, loaded.BaselineProvider);
        Assert.Equal(original.Comparisons.Count, loaded.Comparisons.Count);

        for (int i = 0; i < original.Comparisons.Count; i++)
        {
            Assert.Equal(original.Comparisons[i].Provider, loaded.Comparisons[i].Provider);
            Assert.Equal(original.Comparisons[i].P50Milliseconds, loaded.Comparisons[i].P50Milliseconds, precision: 2);
            Assert.Equal(original.Comparisons[i].SpeedupFactor, loaded.Comparisons[i].SpeedupFactor, precision: 4);
        }
    }

    // ─── Evidence Markdown Export ──────────────────────────────────────

    [Fact]
    public async Task ExportMarkdownAsync_EvidenceReport_CreatesReadableMarkdown()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();
        string outputPath = Path.Join(_tempDir, "evidence.md");

        await BenchmarkReportExporter.ExportMarkdownAsync(report, outputPath);

        Assert.True(File.Exists(outputPath));
        string markdown = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Benchmark Evidence Report", markdown);
        Assert.Contains("full-pipeline", markdown);
        Assert.Contains("Completed", markdown);
        Assert.Contains("pipeline", markdown);
        Assert.Contains("peakWorkingSetBytes", markdown);
        Assert.Contains("audio-prep", markdown);
        Assert.Contains("separation", markdown);
    }

    [Fact]
    public void RenderEvidenceMarkdown_ContainsTimingTable()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();

        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(report);

        Assert.Contains("## Timing Metrics (ms)", markdown);
        Assert.Contains("| pipeline |", markdown);
        Assert.Contains("1234.56", markdown);
    }

    [Fact]
    public void RenderEvidenceMarkdown_ContainsMemoryTable()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();

        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(report);

        Assert.Contains("## Memory Metrics", markdown);
        Assert.Contains("peakWorkingSetBytes", markdown);
        Assert.Contains("MB", markdown);
    }

    [Fact]
    public void RenderEvidenceMarkdown_ContainsResourceValidationTable()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport() with
        {
            ResourceTelemetryBounds = new Trackdub.Domain.Benchmarking.ResourceTelemetryBounds { MaxCpuPercent = 75 },
            ResourceValidationStatus = Trackdub.Domain.Benchmarking.ResourceTelemetryStatus.Failed,
            ResourceTelemetry =
            [
                new BenchmarkStageResourceTelemetry
                {
                    Stage = "audio-prep",
                    Phase = "measured",
                    Iteration = 2,
                    Attempt = 1,
                    ExecutionStatus = BenchmarkEvidenceStatus.Completed,
                    Validation = new Trackdub.Domain.Benchmarking.ResourceTelemetryValidation
                    {
                        Status = Trackdub.Domain.Benchmarking.ResourceTelemetryStatus.Failed,
                        Checks =
                        [
                            new Trackdub.Domain.Benchmarking.ResourceTelemetryCheck(
                                "cpuPercent",
                                Trackdub.Domain.Benchmarking.ResourceTelemetryStatus.Failed,
                                ObservedValue: 91.5,
                                Threshold: 75,
                                Reason: "Configured upper bound exceeded."),
                            new Trackdub.Domain.Benchmarking.ResourceTelemetryCheck(
                                "availableVramMb",
                                Trackdub.Domain.Benchmarking.ResourceTelemetryStatus.Unavailable,
                                ObservedValue: null,
                                Threshold: null,
                                Reason: "No VRAM reader is registered for this host."),
                        ],
                    },
                },
            ],
        };

        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(report);

        Assert.Contains("## Resource Validation", markdown);
        Assert.Contains("| audio-prep | measured | 2 | 1 | cpuPercent | 91.5 | 75 | Failed | Configured upper bound exceeded. |", markdown);
        // An unavailable metric renders its cause instead of a fabricated zero.
        Assert.Contains("| availableVramMb | unavailable | not configured | Unavailable | No VRAM reader is registered for this host. |", markdown);
    }

    [Fact]
    public void RenderEvidenceMarkdown_OmitsResourceValidationSectionWithoutTelemetry()
    {
        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(CreateSampleEvidenceReport());

        Assert.DoesNotContain("## Resource Validation", markdown);
    }

    [Fact]
    public void RenderEvidenceMarkdown_ContainsStageTable()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();

        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(report);

        Assert.Contains("## Stage Results", markdown);
        Assert.Contains("audio-prep", markdown);
        Assert.Contains("100.5", markdown);
    }

    // ─── Matrix Markdown Export ────────────────────────────────────────

    [Fact]
    public async Task ExportMarkdownAsync_MatrixReport_CreatesReadableMarkdown()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();
        string outputPath = Path.Join(_tempDir, "matrix.md");

        await BenchmarkReportExporter.ExportMarkdownAsync(report, outputPath);

        Assert.True(File.Exists(outputPath));
        string markdown = await File.ReadAllTextAsync(outputPath);
        Assert.Contains("# Execution Provider Matrix Report", markdown);
        Assert.Contains("full-pipeline", markdown);
        Assert.Contains("cpu", markdown);
        Assert.Contains("directml", markdown);
        Assert.Contains("tensorrt", markdown);
    }

    [Fact]
    public void RenderMatrixMarkdown_ContainsComparisonTable()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();

        string markdown = BenchmarkReportExporter.RenderMatrixMarkdown(report);

        Assert.Contains("## Provider Comparison", markdown);
        Assert.Contains("| Provider |", markdown);
        Assert.Contains("| cpu |", markdown);
        Assert.Contains("| directml |", markdown);
        Assert.Contains("| tensorrt |", markdown);
    }

    [Fact]
    public void RenderMatrixMarkdown_ShowsSpeedupValues()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();

        string markdown = BenchmarkReportExporter.RenderMatrixMarkdown(report);

        // CPU baseline should have 1.00x speedup
        Assert.Contains("1.00x", markdown);
        // DirectML should have 2.00x speedup (100/50)
        Assert.Contains("2.00x", markdown);
        // TensorRT should have 4.00x speedup (100/25)
        Assert.Contains("4.00x", markdown);
    }

    // ─── ExportAll ────────────────────────────────────────────────────

    [Fact]
    public async Task ExportAllAsync_EvidenceReport_CreatesBothFiles()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();
        string dir = Path.Join(_tempDir, "export-all-evidence");

        await BenchmarkReportExporter.ExportAllAsync(report, dir);

        Assert.True(File.Exists(Path.Join(dir, "benchmark-evidence.json")));
        Assert.True(File.Exists(Path.Join(dir, "benchmark-evidence.md")));
    }

    [Fact]
    public async Task ExportAllAsync_MatrixReport_CreatesBothFiles()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();
        string dir = Path.Join(_tempDir, "export-all-matrix");

        await BenchmarkReportExporter.ExportAllAsync(report, dir);

        Assert.True(File.Exists(Path.Join(dir, "execution-provider-matrix.json")));
        Assert.True(File.Exists(Path.Join(dir, "execution-provider-matrix.md")));
    }

    [Fact]
    public async Task ExportAllAsync_CustomFileName_UsesSpecifiedBaseName()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();
        string dir = Path.Join(_tempDir, "export-custom-name");

        await BenchmarkReportExporter.ExportAllAsync(report, dir, "my-report");

        Assert.True(File.Exists(Path.Join(dir, "my-report.json")));
        Assert.True(File.Exists(Path.Join(dir, "my-report.md")));
    }

    // ─── Validation ────────────────────────────────────────────────────

    [Fact]
    public async Task ValidateMatrixJsonAsync_ValidFile_ReturnsNull()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();
        string path = Path.Join(_tempDir, "validate-ok.json");
        await BenchmarkReportExporter.ExportJsonAsync(report, path);

        string? error = await BenchmarkReportExporter.ValidateMatrixJsonAsync(path);

        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateMatrixJsonAsync_EmptyFile_ReturnsError()
    {
        string path = Path.Join(_tempDir, "validate-empty.json");
        await File.WriteAllTextAsync(path, "");

        string? error = await BenchmarkReportExporter.ValidateMatrixJsonAsync(path);

        Assert.NotNull(error);
        Assert.Contains("JSON deserialization failed", error);
    }

    [Fact]
    public async Task ValidateMatrixJsonAsync_MissingScenario_ReturnsError()
    {
        // Construct a JSON with empty scenario
        string json = """{"Scenario":"","BaselineProvider":"cpu","Comparisons":[{"Provider":"cpu","P50Milliseconds":10,"SpeedupFactor":1,"LatencyDeltaMilliseconds":0,"ThroughputRatio":1,"PeakWorkingSetDeltaBytes":0,"ManagedAllocatedDeltaBytes":0}],"Timestamp":"2026-01-15T12:00:00+00:00"}""";
        string path = Path.Join(_tempDir, "validate-no-scenario.json");
        await File.WriteAllTextAsync(path, json);

        string? error = await BenchmarkReportExporter.ValidateMatrixJsonAsync(path);

        Assert.NotNull(error);
        Assert.Contains("scenario", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task ValidateEvidenceJsonAsync_ValidFile_ReturnsNull()
    {
        BenchmarkEvidenceReport report = CreateSampleEvidenceReport();
        string path = Path.Join(_tempDir, "validate-evidence-ok.json");
        await BenchmarkReportExporter.ExportJsonAsync(report, path);

        string? error = await BenchmarkReportExporter.ValidateEvidenceJsonAsync(path);

        Assert.Null(error);
    }

    [Fact]
    public async Task ValidateEvidenceJsonAsync_MalformedJson_ReturnsError()
    {
        string path = Path.Join(_tempDir, "validate-evidence-bad.json");
        await File.WriteAllTextAsync(path, "{not valid json!!!");

        string? error = await BenchmarkReportExporter.ValidateEvidenceJsonAsync(path);

        Assert.NotNull(error);
        Assert.Contains("JSON deserialization failed", error);
    }

    // ─── Edge Cases ────────────────────────────────────────────────────

    [Fact]
    public void RenderMatrixMarkdown_EmptyComparisons_ProducesHeaderOnly()
    {
        var report = new ExecutionProviderMatrixReport(
            "test-scenario", "cpu", [], DateTimeOffset.UtcNow, []);

        string markdown = BenchmarkReportExporter.RenderMatrixMarkdown(report);

        Assert.Contains("# Execution Provider Matrix Report", markdown);
        Assert.Contains("## Provider Comparison", markdown);
        // Table header exists but no data rows
        Assert.Contains("| Provider |", markdown);
        Assert.DoesNotContain("| cpu |", markdown);
    }

    [Fact]
    public void RenderEvidenceMarkdown_NoTimingsOrMemory_OmitsTables()
    {
        var report = new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = "minimal",
            RunMode = "fresh-process",
            Status = BenchmarkEvidenceStatus.Completed,
            CompletedAtUtc = DateTimeOffset.UtcNow,
        };

        string markdown = BenchmarkReportExporter.RenderEvidenceMarkdown(report);

        Assert.Contains("# Benchmark Evidence Report", markdown);
        Assert.Contains("minimal", markdown);
        Assert.DoesNotContain("## Timing Metrics", markdown);
        Assert.DoesNotContain("## Memory Metrics", markdown);
        Assert.DoesNotContain("## Stage Results", markdown);
    }

    [Fact]
    public async Task ExportJsonAsync_CreatesParentDirectories()
    {
        ExecutionProviderMatrixReport report = CreateSampleMatrixReport();
        string deepPath = Path.Join(_tempDir, "a", "b", "c", "report.json");

        await BenchmarkReportExporter.ExportJsonAsync(report, deepPath);

        Assert.True(File.Exists(deepPath));
    }
}
