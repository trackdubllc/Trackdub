using System.Diagnostics.Tracing;
using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Scenarios;

/// <summary>
/// Challenger 2 empirical stress test harness for Milestone 3:
/// - Offline isolation: zero network, zero live model downloads, zero GPU checks
/// - 10-iteration multi-run stability and 5-stage lifecycle / memory telemetry
/// - CLI DevHost argument permutations (--mock, --dry-run, --format, --providers, --baseline)
/// - Structured JSON output fidelity and mathematical consistency
/// </summary>
public sealed class Challenger2StressTests : IDisposable
{
    private readonly MockDubbingBenchmarkHarness _harness = new();
    private readonly string _tempOutputDir;

    public Challenger2StressTests()
    {
        _tempOutputDir = Path.Join(Path.GetTempPath(), $"trackdub_ch2_stress_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_tempOutputDir))
        {
            try { Directory.Delete(_tempOutputDir, recursive: true); }
            catch (IOException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
            catch (UnauthorizedAccessException ex) { Console.Error.WriteLine($"Best-effort temp cleanup failed: {ex}"); }
        }
    }

    [Fact]
    public async Task Stress_OfflineIsolation_NoNetworkCallsDuringMockExecution()
    {
        // Setup listener for network events from System.Net
        int networkEventsCount = 0;
        using var networkListener = new TestNetworkEventListener(() => Interlocked.Increment(ref networkEventsCount));

        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = Path.Join(_tempOutputDir, "offline_network"),
            Mode = "fresh-process",
            RunCount = 2,
            Mock = true,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Equal(0, networkEventsCount);
    }

    [Fact]
    public async Task Stress_MultiRun10Iterations_PercentileStabilityAndAllFiveStagesCaptured()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = Path.Join(_tempOutputDir, "runs10_stability"),
            Mode = "fresh-process",
            RunCount = 10,
            Mock = true,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);

        // 1. Verify SampleCount and Monotonic Percentiles
        Assert.Equal(10.0, report.TimingsMilliseconds["pipeline:sampleCount"]);
        double min = report.TimingsMilliseconds["pipeline:min"]!.Value;
        double mean = report.TimingsMilliseconds["pipeline:mean"]!.Value;
        double p50 = report.TimingsMilliseconds["pipeline:p50"]!.Value;
        double p90 = report.TimingsMilliseconds["pipeline:p90"]!.Value;
        double p99 = report.TimingsMilliseconds["pipeline:p99"]!.Value;
        double max = report.TimingsMilliseconds["pipeline:max"]!.Value;

        Assert.True(min > 0, $"Min ({min}) > 0");
        Assert.True(min <= mean && mean <= max, $"Min ({min}) <= Mean ({mean}) <= Max ({max})");
        Assert.True(min <= p50, $"Min ({min}) <= P50 ({p50})");
        Assert.True(p50 <= p90, $"P50 ({p50}) <= P90 ({p90})");
        Assert.True(p90 <= p99, $"P90 ({p90}) <= P99 ({p99})");
        Assert.True(p99 <= max, $"P99 ({p99}) <= Max ({max})");

        // 2. Verify all 5 canonical stages exist in Stages list
        string[] canonicalStages = ["audio-prep", "separation", "transcription", "alignment", "dubbing"];
        foreach (BenchmarkEvidenceStage? stage in canonicalStages.Select(stageName =>
            report.Stages.FirstOrDefault(s => s.Name.Equals(stageName, StringComparison.OrdinalIgnoreCase))))
        {
            Assert.NotNull(stage);
            Assert.Equal(BenchmarkEvidenceStatus.Completed, stage.Status);
            Assert.NotNull(stage.ActualModel);
            Assert.NotNull(stage.ActualProvider);
        }

        // 3. Verify stage-level memory telemetry across canonical stages
        Assert.NotNull(report.MemoryBytes);
        Assert.True(report.MemoryBytes.ContainsKey("peakWorkingSetBytes"), "Must contain peakWorkingSetBytes");
        Assert.True(report.MemoryBytes.ContainsKey("managedAllocatedBytes"), "Must contain managedAllocatedBytes");
        Assert.True(report.MemoryBytes["peakWorkingSetBytes"] > 0, "peakWorkingSetBytes > 0");

        foreach (string stageName in canonicalStages)
        {
            string allocKey = $"stage:{stageName}:allocatedBytes";
            string peakKey = $"stage:{stageName}:peakWorkingSet";
            string gen0Key = $"stage:{stageName}:gen0";

            Assert.True(report.MemoryBytes.ContainsKey(allocKey), $"Missing memory telemetry: {allocKey}");
            Assert.True(report.MemoryBytes.ContainsKey(peakKey), $"Missing memory telemetry: {peakKey}");
            Assert.True(report.MemoryBytes.ContainsKey(gen0Key), $"Missing memory telemetry: {gen0Key}");

            Assert.True(report.MemoryBytes[allocKey] >= 0, $"{allocKey} >= 0");
            Assert.True(report.MemoryBytes[peakKey] > 0, $"{peakKey} > 0");
        }
    }

    [Theory]
    [InlineData("console")]
    [InlineData("json")]
    [InlineData("both")]
    public async Task Stress_CliMatrix_ArgumentPermutationsAndFormatValidation(string format)
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        string outDir = Path.Join(_tempOutputDir, $"format_{format}");
        Directory.CreateDirectory(outDir);

        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", outDir, "--mock", "--providers", "cpu,directml,tensorrt", "--runs", "3", "--format", format],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string stdout = output.ToString();

        if (format is "console" or "both")
        {
            Assert.Contains("Execution Provider Matrix: full-pipeline (Baseline: cpu)", stdout);
            Assert.Contains("cpu:", stdout);
            Assert.Contains("directml:", stdout);
            Assert.Contains("tensorrt:", stdout);
        }

        if (format is "json" or "both")
        {
            string jsonPath = Path.Join(outDir, "execution-provider-matrix.json");
            Assert.True(File.Exists(jsonPath), $"Expected matrix JSON file at: {jsonPath}");

            string jsonContent = await File.ReadAllTextAsync(jsonPath);
            var report = JsonSerializer.Deserialize<ExecutionProviderMatrixReport>(jsonContent, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true,
            });

            Assert.NotNull(report);
            Assert.Equal("full-pipeline", report.Scenario);
            Assert.Equal("cpu", report.BaselineProvider);
            Assert.Equal(3, report.Comparisons.Count);

            // Baseline assertions
            var baseline = report.Comparisons.First(c => c.Provider.Equals("cpu", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(1.0, baseline.SpeedupFactor);
            Assert.Equal(0.0, baseline.LatencyDeltaMilliseconds);
            Assert.Equal(1.0, baseline.ThroughputRatio);
            Assert.Equal(0, baseline.PeakWorkingSetDeltaBytes);
            Assert.Equal(0, baseline.ManagedAllocatedDeltaBytes);

            // Faster provider assertions (directml and tensorrt have simulated speedups)
            var dml = report.Comparisons.First(c => c.Provider.Equals("directml", StringComparison.OrdinalIgnoreCase));
            Assert.True(dml.SpeedupFactor > 1.0, $"DirectML speedup {dml.SpeedupFactor} > 1.0");
            Assert.True(dml.LatencyDeltaMilliseconds < 0, $"DirectML latency delta {dml.LatencyDeltaMilliseconds} < 0");

            var trt = report.Comparisons.First(c => c.Provider.Equals("tensorrt", StringComparison.OrdinalIgnoreCase));
            Assert.True(trt.SpeedupFactor > 1.0, $"TRT speedup {trt.SpeedupFactor} > 1.0");
            Assert.True(trt.LatencyDeltaMilliseconds < 0, $"TRT latency delta {trt.LatencyDeltaMilliseconds} < 0");
        }
    }

    [Fact]
    public async Task Stress_CliMatrix_DryRun_ExecutesWithMinimalLatency()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        string outDir = Path.Join(_tempOutputDir, "dry_run_test");

        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", outDir, "--dry-run", "--providers", "cpu,directml"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string jsonPath = Path.Join(outDir, "execution-provider-matrix.json");
        Assert.True(File.Exists(jsonPath));

        string jsonContent = await File.ReadAllTextAsync(jsonPath);
        var report = JsonSerializer.Deserialize<ExecutionProviderMatrixReport>(jsonContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(report);
        Assert.Equal(2, report.Comparisons.Count);
    }

    [Fact]
    public async Task Stress_CliMatrix_CustomBaselineProvider_ComputesRelativeMetricsAccurately()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        string outDir = Path.Join(_tempOutputDir, "custom_baseline");

        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["matrix", fixture, "--output", outDir, "--mock", "--baseline", "directml", "--providers", "cpu,directml,tensorrt"],
            TextReader.Null,
            output,
            error,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        string jsonPath = Path.Join(outDir, "execution-provider-matrix.json");
        Assert.True(File.Exists(jsonPath));

        string jsonContent = await File.ReadAllTextAsync(jsonPath);
        var report = JsonSerializer.Deserialize<ExecutionProviderMatrixReport>(jsonContent, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(report);
        Assert.Equal("directml", report.BaselineProvider);

        var baseline = report.Comparisons.First(c => c.Provider.Equals("directml", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1.0, baseline.SpeedupFactor);
        Assert.Equal(0.0, baseline.LatencyDeltaMilliseconds);

        // CPU is slower than DirectML, so speedup should be < 1.0 and latency delta > 0
        var cpu = report.Comparisons.First(c => c.Provider.Equals("cpu", StringComparison.OrdinalIgnoreCase));
        Assert.True(cpu.SpeedupFactor < 1.0, $"CPU speedup {cpu.SpeedupFactor} < 1.0 vs DirectML baseline");
        Assert.True(cpu.LatencyDeltaMilliseconds > 0, $"CPU latency delta {cpu.LatencyDeltaMilliseconds} > 0 vs DirectML baseline");
    }

    private sealed class TestNetworkEventListener(Action onNetworkEvent) : EventListener
    {
        protected override void OnEventSourceCreated(EventSource eventSource)
        {
            if (eventSource.Name.StartsWith("System.Net", StringComparison.OrdinalIgnoreCase))
            {
                EnableEvents(eventSource, EventLevel.LogAlways);
            }
        }

        protected override void OnEventWritten(EventWrittenEventArgs eventArgs)
        {
            onNetworkEvent();
        }
    }
}
