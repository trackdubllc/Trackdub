using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;

namespace Trackdub.Benchmarks.Tests;

public sealed class ControlledDubbingBenchmarkRunnerTests
{
    [Fact]
    public async Task Controlled_run_includes_resource_validation_in_evidenceAsync()
    {
        string directory = Path.Join(Path.GetTempPath(), $"telemetry-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string fixture = Path.Join(directory, "fixture.wav");
        await File.WriteAllBytesAsync(fixture, [1, 2, 3]);
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());
            var report = await runner.RunAsync(new ControlledDubbingBenchmarkOptions
            {
                FixturePath = fixture,
                OutputDirectory = Path.Join(directory, "output"),
                Mock = true,
                Stage = "audio-preparation",
            });
            Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
            var json = System.Text.Json.JsonSerializer.SerializeToElement(report);
            Assert.True(json.TryGetProperty("ResourceTelemetry", out var telemetry));
            Assert.NotEmpty(telemetry.EnumerateArray());
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task Unknown_model_alias_is_rejected_before_running()
    {
        string fixture = Path.GetTempFileName();
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());

            // "whisper-onnx" is an engine family, not a model alias; the pipeline would
            // silently plan its default ASR model and the report would measure the wrong one.
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = Path.GetTempPath(),
                    Stage = "asr",
                    Model = "whisper-onnx",
                }));

            Assert.Contains("whisper-onnx", error.Message, StringComparison.Ordinal);
            Assert.Contains("whisper-small", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task Model_alias_for_a_different_task_is_rejected()
    {
        string fixture = Path.GetTempFileName();
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());

            // "kokoro" is a valid alias, but for TTS: the planner would keep its default ASR
            // model while the report claims the requested one.
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = Path.GetTempPath(),
                    Stage = "asr",
                    Model = "kokoro",
                }));

            Assert.Contains("kokoro", error.Message, StringComparison.Ordinal);
            Assert.Contains("asr", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    [Fact]
    public async Task Model_task_mismatch_message_pins_the_required_task_for_non_obvious_stages()
    {
        string fixture = Path.GetTempFileName();
        try
        {
            using var runner = new ControlledDubbingBenchmarkRunner(new NoHistory());

            // "kokoro" is a TTS model; lip-sync requires the forced-alignment task. Asserting
            // the message pins ManifestTaskFor's non-obvious LipSync arm (stage string
            // "lip-sync" → task "forced-alignment"), which no rejection test covers today.
            ArgumentException error = await Assert.ThrowsAsync<ArgumentException>(() => runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = fixture,
                    OutputDirectory = Path.GetTempPath(),
                    Stage = "lip-sync",
                    Model = "kokoro",
                }));

            Assert.Contains("forced-alignment", error.Message, StringComparison.Ordinal);
        }
        finally
        {
            File.Delete(fixture);
        }
    }

    private sealed class NoHistory : IBenchmarkEvidenceRepository
    {
        public Task SaveAsync(BenchmarkEvidenceReport report, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<BenchmarkEvidenceReport?> GetAsync(Guid runId, CancellationToken cancellationToken = default) =>
            Task.FromResult<BenchmarkEvidenceReport?>(null);

        public Task<IReadOnlyList<BenchmarkEvidenceReport>> ListRecentAsync(
            BenchmarkEvidenceKind? kind, int limit, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkEvidenceReport>>([]);
    }
}
