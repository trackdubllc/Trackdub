using Trackdub.Benchmarks;
using Trackdub.Benchmarks.Scenarios;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.Scenarios;

public sealed class MockPipelineExecutionTests : IDisposable
{
    private readonly MockDubbingBenchmarkHarness _harness = new();
    private readonly string _tempOutputDir;

    public MockPipelineExecutionTests()
    {
        _tempOutputDir = Path.Join(Path.GetTempPath(), $"trackdub_mock_test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempOutputDir);
    }

    public void Dispose()
    {
        _harness.Dispose();
        if (Directory.Exists(_tempOutputDir))
        {
            try { Directory.Delete(_tempOutputDir, recursive: true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public async Task MockExecution_CompletesSuccessfully_WithoutModelsOrGpu()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = _tempOutputDir,
            Mode = "fresh-process",
            RunCount = 1,
            Mock = true,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.NotNull(report.TimingsMilliseconds);
        Assert.True(report.TimingsMilliseconds["pipeline"] > 0);
    }

    [Fact]
    public async Task MockExecution_CapturesAllFiveCanonicalStages()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = _tempOutputDir,
            Mode = "fresh-process",
            RunCount = 1,
            Mock = true,
        });

        string[] expectedStages = ["audio-prep", "separation", "transcription", "alignment", "dubbing"];
        foreach (BenchmarkEvidenceStage? stage in expectedStages.Select(expected =>
            report.Stages.FirstOrDefault(s => s.Name.Equals(expected, StringComparison.OrdinalIgnoreCase))))
        {
            Assert.NotNull(stage);
            Assert.Equal(BenchmarkEvidenceStatus.Completed, stage.Status);
            Assert.True(stage.DurationMilliseconds >= 0);
        }
    }

    [Fact]
    public async Task MockExecution_CollectsGranularStageMemoryAndAllocations()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = _tempOutputDir,
            Mode = "fresh-process",
            RunCount = 1,
            Mock = true,
        });

        Assert.NotNull(report.MemoryBytes);
        Assert.True(report.MemoryBytes.ContainsKey("processWorkingSetStart"));
        Assert.True(report.MemoryBytes.ContainsKey("peakWorkingSetBytes"));
        Assert.True(report.MemoryBytes.ContainsKey("managedAllocatedBytes"));
        Assert.True(report.MemoryBytes["peakWorkingSetBytes"] > 0);
    }

    [Fact]
    public async Task MockExecution_MultiRunIterations_CalculatesAccuratePercentiles()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = _tempOutputDir,
            Mode = "fresh-process",
            RunCount = 3,
            Mock = true,
        });

        Assert.Equal(3.0, report.TimingsMilliseconds["pipeline:sampleCount"]);
        double min = report.TimingsMilliseconds["pipeline:min"]!.Value;
        double p50 = report.TimingsMilliseconds["pipeline:p50"]!.Value;
        double p90 = report.TimingsMilliseconds["pipeline:p90"]!.Value;
        double p99 = report.TimingsMilliseconds["pipeline:p99"]!.Value;
        double max = report.TimingsMilliseconds["pipeline:max"]!.Value;

        Assert.True(min <= p50, $"Min ({min}) <= P50 ({p50})");
        Assert.True(p50 <= p90, $"P50 ({p50}) <= P90 ({p90})");
        Assert.True(p90 <= p99, $"P90 ({p90}) <= P99 ({p99})");
        Assert.True(p99 <= max, $"P99 ({p99}) <= Max ({max})");
    }

    [Fact]
    public async Task MockExecution_ConfigurableSimulatedStageLatencies()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services, opts =>
            {
                opts.SimulatedStageLatencies["audio-prep"] = TimeSpan.FromMilliseconds(25);
                opts.SimulatedStageLatencies["transcription"] = TimeSpan.FromMilliseconds(50);
            }));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = _tempOutputDir,
            Mode = "fresh-process",
            RunCount = 1,
            Mock = true,
        });

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        BenchmarkEvidenceStage? prep = report.Stages.FirstOrDefault(s => s.Name == "audio-prep");
        BenchmarkEvidenceStage? asr = report.Stages.FirstOrDefault(s => s.Name == "transcription");

        Assert.NotNull(prep);
        Assert.NotNull(asr);
        Assert.True(prep.DurationMilliseconds >= 15, $"Prep duration {prep.DurationMilliseconds} >= 15ms");
        Assert.True(asr.DurationMilliseconds >= 30, $"ASR duration {asr.DurationMilliseconds} >= 30ms");
    }

    [Fact]
    public async Task MockExecution_SimulatedStageFailure_RecordsFailureStatusAndReason()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services, opts =>
            {
                opts.FailStage = "transcription";
                opts.FailureReason = "Simulated ASR failure";
            }));

        BenchmarkEvidenceReport report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
        {
            FixturePath = fixture,
            OutputDirectory = _tempOutputDir,
            Mode = "fresh-process",
            RunCount = 1,
            Mock = true,
        });

        Assert.True(report.Status is BenchmarkEvidenceStatus.Failed or BenchmarkEvidenceStatus.PartiallyCompleted,
            $"Expected Failed or PartiallyCompleted, got {report.Status}");
        Assert.True(
            (report.Reason is not null && report.Reason.Contains("Simulated ASR failure", StringComparison.OrdinalIgnoreCase)) ||
            report.Stages.Any(s => s.Reason is not null && s.Reason.Contains("Simulated ASR failure", StringComparison.OrdinalIgnoreCase)));
    }

    [Fact]
    public async Task MockExecution_Cancellation_HaltsExecutionAndRecordsCanceledStatus()
    {
        string fixture = _harness.CreateTempAudioFixture(durationSeconds: 1.0);
        using var runner = new ControlledDubbingBenchmarkRunner(
            serviceConfigurator: services => MockDubbingPipelineServices.ConfigureMockPipeline(services));

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        try
        {
            var report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = _tempOutputDir,
                Mode = "fresh-process",
                RunCount = 1,
                Mock = true,
            }, cts.Token);

            Assert.Equal(BenchmarkEvidenceStatus.Canceled, report.Status);
        }
        catch (OperationCanceledException)
        {
            // Also acceptable per specification
        }
    }
}
