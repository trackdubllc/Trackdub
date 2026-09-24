using Trackdub.Benchmarks;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Persistence;

namespace Trackdub.Benchmarks.Tests;

public sealed class ControlledDubbingBenchmarkRunnerTests
{
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
