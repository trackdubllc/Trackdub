using System.Text.Json;
using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Runtime.Routing;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Benchmarks.Tests;

public sealed class TtsEvalRunnerTests
{
    [Fact]
    public void ReadJobs_ParsesSnakeCaseLinesAndSkipsCommentsAndBlanks()
    {
        using var reader = new StringReader("""
            # comment
            {"id":"j1","text":"Hello.","language_code":"en-us","voice_id":"af_heart","speed":1.1}
            """);
        TtsEvalJob job = Assert.Single(TtsEvalRunner.ReadJobs(reader));
        Assert.Equal(new TtsEvalJob("j1", "Hello.", "en-us", "af_heart", 1.1f, null, null), job);
    }

    [Theory]
    [InlineData("not json")]
    [InlineData("""{"id":"a","text":"x"}""")]
    [InlineData("""{"id":" ","text":"x","language_code":"en-us","voice_id":"af_heart"}""")]
    public void ReadJobs_RejectsMalformedLines(string line)
    {
        using var reader = new StringReader(line);
        Assert.Throws<InvalidDataException>(() => TtsEvalRunner.ReadJobs(reader));
    }

    [Fact]
    public void ReadJobs_RejectsDuplicateIds()
    {
        const string line = """{"id":"a","text":"x","language_code":"en-us","voice_id":"af_heart"}""";
        using var reader = new StringReader(line + "\n" + line);
        Assert.Throws<InvalidDataException>(() => TtsEvalRunner.ReadJobs(reader));
    }

    [Theory]
    [InlineData("""{"id":"a","text":"x","language_code":"en-us","voice_id":"af_heart","warmup_runs":-1}""")]
    [InlineData("""{"id":"a","text":"x","language_code":"en-us","voice_id":"af_heart","repeat_runs":0}""")]
    [InlineData("""{"id":"a","text":"x","language_code":"en-us","voice_id":"af_heart","repeat_runs":-3}""")]
    public void ReadJobs_RejectsInvalidPerJobRunOverrides(string line)
    {
        using var reader = new StringReader(line);
        Assert.Throws<InvalidDataException>(() => TtsEvalRunner.ReadJobs(reader));
    }

    [Fact]
    public void TryParse_AcceptsRequiredAndOptionalArguments()
    {
        bool ok = TtsEvalOptions.TryParse(
            ["--jobs", "j.jsonl", "--results", "r.jsonl", "--provider", "cpu", "--model", "kokoro",
             "--model-directory", "m", "--model-cache-directory", "c", "--warmup-runs", "1", "--repeat-runs", "5"],
            TextWriter.Null, out TtsEvalOptions options);
        Assert.True(ok);
        Assert.Equal("j.jsonl", options.JobsPath);
        Assert.Equal("r.jsonl", options.ResultsPath);
        Assert.Equal("cpu", options.Provider);
        Assert.Equal("kokoro", options.Model);
        Assert.Equal("m", options.ModelDirectory);
        Assert.Equal("c", options.ModelCacheDirectory);
        Assert.Equal(1, options.WarmupRuns);
        Assert.Equal(5, options.RepeatRuns);
    }

    [Fact]
    public void TryParse_DefaultsModelWarmupAndRepeats()
    {
        bool ok = TtsEvalOptions.TryParse(
            ["--jobs", "j.jsonl", "--results", "r.jsonl"],
            TextWriter.Null, out TtsEvalOptions options);
        Assert.True(ok);
        Assert.Equal("kokoro", options.Model);
        Assert.Equal(TtsEvalOptions.DefaultWarmupRuns, options.WarmupRuns);
        Assert.Equal(TtsEvalOptions.DefaultRepeatRuns, options.RepeatRuns);
    }

    [Theory]
    [InlineData("--jobs", "j.jsonl")]
    [InlineData("--jobs", "j", "--results", "r", "--provider", "warp-drive")]
    [InlineData("--jobs", "j", "--results", "r", "--warmup-runs", "-1")]
    [InlineData("--jobs", "j", "--results", "r", "--repeat-runs", "0")]
    [InlineData("--jobs", "j", "--results", "r", "--bogus", "x")]
    [InlineData("--jobs")]
    public void TryParse_RejectsBadArguments(params string[] args)
    {
        using var error = new StringWriter();
        Assert.False(TtsEvalOptions.TryParse(args, error, out _));
        Assert.NotEmpty(error.ToString());
    }

    [Fact]
    public async Task ProgramRunAsync_DispatchesTtsBenchHelpCaseInsensitively()
    {
        using var output = new StringWriter();
        using var error = new StringWriter();
        int exitCode = await Program.RunAsync(
            ["TTS-Bench", "--help"], TextReader.Null, output, error, CancellationToken.None);
        Assert.Equal(0, exitCode);
        Assert.Contains(TtsEvalOptions.Usage, output.ToString());
        Assert.Empty(error.ToString());
    }

    [Fact]
    public async Task RunJobsAsync_RecordsTimingMemoryAndProviderPerJobAndContinuesAfterFailure()
    {
        var engine = new FakeTtsEngine { FailOn = "Boom text." };
        var sampler = new SequenceSampler(100, 150, 120, 120, 160, 170);
        var jobs = new[]
        {
            new TtsEvalJob("ok1", "Hello there.", "en-us", "af_heart", null, 1, 2),
            new TtsEvalJob("bad", "Boom text.", "en-us", "af_heart", null, 1, 2),
            new TtsEvalJob("ok2", "Second good.", "en-us", "af_heart", null, 1, 2),
        };
        var options = new TtsEvalOptions("j", "r", "cpu", "kokoro", null, null, WarmupRuns: 0, RepeatRuns: 2, ShowHelp: false);
        using var lines = new StringWriter();
        IReadOnlyList<TtsEvalResult> results = await TtsEvalRunner.RunJobsAsync(
            jobs, engine, sampler, options, lines, CancellationToken.None);
        Assert.Equal([true, false, true], results.Select(r => r.Ok));
        Assert.Equal([0, 1, 2], results.Select(r => r.JobIndex));
        Assert.Equal("boom", results[1].Error);
        Assert.Equal(12.0, results[0].AudioSeconds);
        Assert.True(results[0].Rtf > 0);
        Assert.Equal("Cpu", results[0].SelectedProvider);
        Assert.True(results[0].PeakWorkingSetBytes >= 150);
        Assert.True(results[0].WarmupPeakWorkingSetBytes >= 150);
        Assert.Equal("cpu", results[1].RequestedProvider);
        Assert.Equal("kokoro", engine.Requests[0].Options!.PreferredModelAlias);
        Assert.True(engine.Requests[0].Options!.RequirePreferredModelAlias);
        Assert.Equal("cpu", engine.Requests[0].Options!.PreferredExecutionProvider);
        Assert.True(engine.Requests[0].Options!.RequirePreferredExecutionProvider);
        Assert.Equal("af_heart", engine.Requests[0].Voice.VoiceId);
        string[] written = lines.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(3, written.Length);
        using JsonDocument first = JsonDocument.Parse(written[0]);
        Assert.Equal("ok1", first.RootElement.GetProperty("id").GetString());
        Assert.True(first.RootElement.TryGetProperty("best_wall_ms", out _));
        Assert.True(first.RootElement.TryGetProperty("mean_wall_ms", out _));
        Assert.True(first.RootElement.TryGetProperty("warmup_peak_working_set_bytes", out _));
        Assert.True(first.RootElement.TryGetProperty("peak_working_set_bytes", out _));
    }

    [Fact]
    public async Task RunJobsAsync_SamplerFailureDoesNotAbortRemainingJobs()
    {
        using var lines = new StringWriter();
        var engine = new FakeTtsEngine();
        var jobs = new[]
        {
            new TtsEvalJob("a", "Hello.", "en-us", "af_heart", null, 0, 1),
            new TtsEvalJob("b", "World.", "en-us", "af_heart", null, 0, 1),
        };
        var options = new TtsEvalOptions("j", "r", null, "kokoro", null, null, 0, 1, false);
        IReadOnlyList<TtsEvalResult> results = await TtsEvalRunner.RunJobsAsync(
            jobs, engine, new ThrowingSampler(), options, lines, CancellationToken.None);
        Assert.Equal([true, true], results.Select(r => r.Ok));
        Assert.Equal(-1, results[0].WorkingSetBeforeBytes);
        Assert.Null(results[0].WarmupPeakWorkingSetBytes);
        Assert.Null(results[0].PeakWorkingSetBytes);
        Assert.Equal(2, engine.Requests.Count);
    }

    [Fact]
    public async Task RunJobsAsync_WithoutProviderDoesNotRequireOne()
    {
        var engine = new FakeTtsEngine();
        var options = new TtsEvalOptions("j", "r", null, "kokoro", null, null, 0, 1, false);
        await TtsEvalRunner.RunJobsAsync(
            [new TtsEvalJob("x", "Hi.", "en-us", "af_heart", null, null, null)],
            engine, new SequenceSampler(1), options, TextWriter.Null, CancellationToken.None);
        Assert.False(engine.Requests[0].Options!.RequirePreferredExecutionProvider);
        Assert.Null(engine.Requests[0].Options!.PreferredExecutionProvider);
    }

    [Fact]
    public async Task RunJobsAsync_PropagatesCancellation()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var options = new TtsEvalOptions("j", "r", null, "kokoro", null, null, 0, 1, false);
        await Assert.ThrowsAsync<OperationCanceledException>(() => TtsEvalRunner.RunJobsAsync(
            [new TtsEvalJob("x", "Hi.", "en-us", "af_heart", null, null, null)],
            new FakeTtsEngine(), new SequenceSampler(1), options, TextWriter.Null, cts.Token));
    }

    [Fact]
    public async Task RunJobsAsync_RecordsEngineFailureAndContinues()
    {
        using var lines = new StringWriter();
        var engine = new FakeTtsEngine { FailOn = "Boom." };
        var jobs = new[]
        {
            new TtsEvalJob("bad", "Boom.", "en-us", "af_heart", null, 0, 1),
            new TtsEvalJob("next", "Fine.", "en-us", "af_heart", null, 0, 1),
        };
        var options = new TtsEvalOptions("j", "r", null, "kokoro", null, null, 0, 1, false);
        IReadOnlyList<TtsEvalResult> results = await TtsEvalRunner.RunJobsAsync(
            jobs, engine, new SequenceSampler(10, 20), options, lines, CancellationToken.None);
        Assert.Equal([false, true], results.Select(r => r.Ok));
        Assert.False(string.IsNullOrWhiteSpace(results[0].Error));
        Assert.Equal(2, engine.Requests.Count);
        Assert.Contains("bad", lines.ToString());
        Assert.Contains("next", lines.ToString());
    }

    private sealed class SequenceSampler(params long[] values) : IWorkingSetSampler
    {
        private int next;
        public long CaptureWorkingSetBytes() => values[Math.Min(next++, values.Length - 1)];
    }

    private sealed class ThrowingSampler : IWorkingSetSampler
    {
        public long CaptureWorkingSetBytes() => throw new InvalidOperationException("sampler unavailable");
    }

    private sealed class FakeTtsEngine : ITtsEngineAdapter, IStageRuntimeExecutionReporter
    {
        public string? FailOn { get; init; }
        public List<TtsSynthesisRequest> Requests { get; } = [];
        public string EngineFamily => "kokoro";
        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

        public Task<TtsSynthesisResult> SynthesizeAsync(TtsSynthesisRequest request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            if (FailOn is not null && string.Equals(request.Text, FailOn, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("boom");
            }
            LastExecutionSummary = new StageRuntimeExecutionSummary("Cpu", "Cpu");
            return Task.FromResult(new TtsSynthesisResult(
                WavBytes: [], DurationSamples: 288_000, SampleRate: 24_000,
                ModelId: "kokoro-onnx", VoiceId: request.Voice.VoiceId, Provider: "Cpu"));
        }

        public Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request, StageRuntimePlan plan, CancellationToken cancellationToken)
            => SynthesizeAsync(request, cancellationToken);
    }
}
