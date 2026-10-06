using System.Linq;
using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Benchmarks.Tests;

public sealed class SeparationEvalRunnerTests
{
    [Fact]
    public void ReadJobs_ParsesSnakeCaseLinesAndSkipsCommentsAndBlanks()
    {
        using var reader = new StringReader("""
            # comment

            {"id":"a1","input":"in.wav","vocals_output":"v.wav","bed_output":"b.wav"}
            """);

        SeparationEvalJob job = Assert.Single(SeparationEvalRunner.ReadJobs(reader));

        Assert.Equal(new SeparationEvalJob("a1", "in.wav", "v.wav", "b.wav"), job);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"id":"a","input":"x"}""")]
    [InlineData("""{"id":" ","input":"x","vocals_output":"v","bed_output":"b"}""")]
    public void ReadJobs_RejectsMalformedLines(string line)
    {
        using var reader = new StringReader(line);
        Assert.Throws<InvalidDataException>(() => SeparationEvalRunner.ReadJobs(reader));
    }

    [Fact]
    public void ReadJobs_RejectsDuplicateIds()
    {
        const string line = """{"id":"a","input":"x","vocals_output":"v","bed_output":"b"}""";
        using var reader = new StringReader(line + "\n" + line);
        Assert.Throws<InvalidDataException>(() => SeparationEvalRunner.ReadJobs(reader));
    }

    [Fact]
    public void TryParse_AcceptsRequiredAndOptionalArguments()
    {
        bool ok = SeparationEvalOptions.TryParse(
            ["--jobs", "j.jsonl", "--results", "r.jsonl", "--provider", "cpu", "--model-directory", "m", "--model-cache-directory", "c"],
            TextWriter.Null, out SeparationEvalOptions options);

        Assert.True(ok);
        Assert.Equal("j.jsonl", options.JobsPath);
        Assert.Equal("cpu", options.Provider);
        Assert.Equal("spleeter", options.Model);
        Assert.Equal("m", options.ModelDirectory);
        Assert.Equal("c", options.ModelCacheDirectory);
    }

    [Theory]
    [InlineData("dml", "directml")]
    [InlineData("migraphx", "migraphx")]
    [InlineData("trt-rtx", "trt-rtx")]
    [InlineData("directml", "directml")]
    public void TryParse_AcceptsCanonicalProviderTokensAndAliases(string token, string canonical)
    {
        bool ok = SeparationEvalOptions.TryParse(
            ["--jobs", "j", "--results", "r", "--provider", token],
            TextWriter.Null, out SeparationEvalOptions options);

        Assert.True(ok);
        Assert.Equal(canonical, options.Provider);
    }

    [Theory]
    [InlineData("--jobs", "j.jsonl")]
    [InlineData("--jobs", "j", "--results", "r", "--provider", "warp-drive")]
    [InlineData("--jobs", "j", "--results", "r", "--provider", "999")]
    [InlineData("--jobs", "j", "--results", "r", "--bogus", "x")]
    [InlineData("--jobs")]
    public void TryParse_RejectsBadArguments(params string[] args)
    {
        using var error = new StringWriter();

        Assert.False(SeparationEvalOptions.TryParse(args, error, out _));
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task ProgramRunAsync_DispatchesSeparationEvalHelpCaseInsensitively()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();

        int exitCode = await Program.RunAsync(
            ["Separation-Eval", "--help"], TextReader.Null, output, error, CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.Contains(SeparationEvalOptions.Usage, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunJobsAsync_RecordsTimingMemoryAndProviderPerJobAndContinuesAfterFailure()
    {
        var engine = new FakeEngine { FailOn = "b.wav" };
        var sampler = new SequenceSampler(100, 150, 120, 120, 130, 140);
        var jobs = new[]
        {
            new SeparationEvalJob("ok1", "a.wav", Out("v1"), Out("b1")),
            new SeparationEvalJob("bad", "b.wav", Out("v2"), Out("b2")),
            new SeparationEvalJob("ok2", "c.wav", Out("v3"), Out("b3")),
        };
        using var lines = new StringWriter();

        IReadOnlyList<SeparationEvalResult> results = await SeparationEvalRunner.RunJobsAsync(
            jobs, engine, sampler, "spleeter", "cpu", lines, CancellationToken.None);

        Assert.Equal([true, false, true], results.Select(r => r.Ok));
        Assert.Equal([0, 1, 2], results.Select(r => r.JobIndex));
        Assert.Equal("boom", results[1].Error);
        Assert.Equal(12.0, results[0].AudioSeconds);
        Assert.True(results[0].Rtf > 0);
        Assert.Equal("Cpu", results[0].SelectedProvider);
        Assert.True(results[0].PeakWorkingSetBytes >= 150);
        Assert.Equal("cpu", engine.Requests[0].PreferredExecutionProvider);
        Assert.True(engine.Requests[0].RequirePreferredExecutionProvider);
        Assert.Equal("spleeter", engine.Requests[0].PreferredModelAlias);

        string[] written = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, written.Length);
        using JsonDocument first = JsonDocument.Parse(written[0]);
        Assert.Equal("ok1", first.RootElement.GetProperty("id").GetString());
        Assert.True(first.RootElement.TryGetProperty("wall_ms", out _));
        Assert.True(first.RootElement.TryGetProperty("peak_working_set_bytes", out _));
    }

    [Fact]
    public async Task RunJobsAsync_WithoutProviderDoesNotRequireOne()
    {
        var engine = new FakeEngine();

        await SeparationEvalRunner.RunJobsAsync(
            [new SeparationEvalJob("x", "a.wav", Out("v"), Out("b"))],
            engine, new SequenceSampler(1), "spleeter", provider: null, TextWriter.Null, CancellationToken.None);

        Assert.False(engine.Requests[0].RequirePreferredExecutionProvider);
        Assert.Null(engine.Requests[0].PreferredExecutionProvider);
    }

    [Fact]
    public async Task RunJobsAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() => SeparationEvalRunner.RunJobsAsync(
            [new SeparationEvalJob("x", "a.wav", Out("v"), Out("b"))],
            new FakeEngine(), new SequenceSampler(1), "spleeter", null, TextWriter.Null, cts.Token));
    }

    [Fact]
    public async Task RunJobsAsync_RecordsOutputDirectoryFailureAndContinues()
    {
        using var lines = new StringWriter();
        var jobs = new[]
        {
            new SeparationEvalJob("bad-path", "a.wav", "\0", Out("bad-bed")),
            new SeparationEvalJob("next", "b.wav", Out("next-vocals"), Out("next-bed")),
        };
        var engine = new FakeEngine();

        IReadOnlyList<SeparationEvalResult> results = await SeparationEvalRunner.RunJobsAsync(
            jobs, engine, new SequenceSampler(10, 20), "spleeter", null, lines, CancellationToken.None);

        Assert.Equal([false, true], results.Select(result => result.Ok));
        Assert.False(string.IsNullOrWhiteSpace(results[0].Error));
        Assert.Single(engine.Requests);
        Assert.Contains("bad-path", lines.ToString());
        Assert.Contains("next", lines.ToString());
    }

    [Fact]
    public async Task RunJobsAsync_RecordsUnrequestedCancellationAndContinues()
    {
        using var lines = new StringWriter();
        var engine = new FakeEngine { CancelWithoutCallerRequestOn = "a.wav" };
        var jobs = new[]
        {
            new SeparationEvalJob("cancelled", "a.wav", Out("cancelled-vocals"), Out("cancelled-bed")),
            new SeparationEvalJob("next", "b.wav", Out("next-vocals"), Out("next-bed")),
        };

        IReadOnlyList<SeparationEvalResult> results = await SeparationEvalRunner.RunJobsAsync(
            jobs, engine, new SequenceSampler(10, 20, 30, 40), "spleeter", null, lines, CancellationToken.None);

        Assert.Equal([false, true], results.Select(result => result.Ok));
        Assert.Contains("timed out", results[0].Error);
        Assert.Equal(2, engine.Requests.Count);
    }

    [Fact]
    public async Task RunAsync_ReportsResultsSetupFailureAsControlledError()
    {
        string jobsPath = Path.Join(Path.GetTempPath(), Guid.NewGuid().ToString("N") + ".jsonl");
        await File.WriteAllTextAsync(jobsPath,
            "{\"id\":\"a\",\"input\":\"in.wav\",\"vocals_output\":\"v.wav\",\"bed_output\":\"b.wav\"}\n");
        using var output = new StringWriter();
        using var error = new StringWriter();
        var options = new SeparationEvalOptions(jobsPath, "\0", null, "spleeter", null, null, null, null, false);

        try
        {
            int exitCode = await SeparationEvalRunner.RunAsync(options, output, error, CancellationToken.None);
            Assert.Equal(1, exitCode);
            Assert.Contains("setup failed", error.ToString());
        }
        finally
        {
            File.Delete(jobsPath);
        }
    }

    [Fact]
    public async Task RunJobsAsync_CarriesMonitorDilationWarningOnSuccessAndFailure()
    {
        var engine = new FakeEngine { FailOn = "b.wav" };
        var jobs = new[]
        {
            new SeparationEvalJob("ok", "a.wav", Out("v1"), Out("b1")),
            new SeparationEvalJob("bad", "b.wav", Out("v2"), Out("b2")),
        };
        using var lines = new StringWriter();

        IReadOnlyList<SeparationEvalResult> results = await SeparationEvalRunner.RunJobsAsync(
            jobs, engine, new SequenceSampler(100, 150), "spleeter", "cpu", lines,
            CancellationToken.None, new WarningMonitorFactory("dilated"));

        Assert.Equal(["dilated", "dilated"], results.Select(r => r.PeakWorkingSetSamplingWarning));
        string[] written = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, written.Length);
        foreach (JsonDocument document in written.Select(line => JsonDocument.Parse(line)))
        {
            using JsonDocument disposable = document;
            Assert.Equal("dilated", disposable.RootElement.GetProperty("peak_working_set_sampling_warning").GetString());
        }
    }

    private static string Out(string name) =>
        Path.Join(Path.GetTempPath(), "trackdub-separation-eval-tests", name + ".wav");

    private sealed class WarningMonitorFactory(string warning) : IWorkingSetPeakMonitorFactory
    {
        public IWorkingSetPeakMonitor Create(IWorkingSetSampler sampler, long? initialValue, TimeSpan? interval) =>
            new WarningMonitor(warning);
    }

    private sealed class WarningMonitor(string warning) : IWorkingSetPeakMonitor
    {
        public string? UnavailableReason => null;

        public string? SamplingWarning => warning;

        public long? Stop() => 150;
    }

    private sealed class SequenceSampler(params long[] values) : IWorkingSetSampler
    {
        private int next;

        public long CaptureWorkingSetBytes() => values[Math.Min(next++, values.Length - 1)];
    }

    private sealed class FakeEngine : IStemSeparationEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string? FailOn { get; init; }

        public string? CancelWithoutCallerRequestOn { get; init; }

        public List<StemSeparationRequest> Requests { get; } = [];

        public string EngineFamily => "spleeter";

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<StemSeparationResult> SeparateAsync(
            StemSeparationRequest request, IProgress<StemSeparationProgress>? progress, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (CancelWithoutCallerRequestOn is not null &&
                string.Equals(request.SourceAudioPath, CancelWithoutCallerRequestOn, StringComparison.Ordinal))
            {
                throw new TaskCanceledException("engine timed out");
            }

            if (FailOn is not null && string.Equals(request.SourceAudioPath, FailOn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom");
            }

            LastExecutionSummary = new StageRuntimeExecutionSummary("Cpu", "Cpu");
            return Task.FromResult(new StemSeparationResult(12.0, 44100, 1));
        }

        public Task<StemSeparationResult> SeparateAsync(
            StemSeparationRequest request, StageRuntimePlan plan, IProgress<StemSeparationProgress>? progress,
            CancellationToken cancellationToken) => SeparateAsync(request, progress, cancellationToken);
    }
}
