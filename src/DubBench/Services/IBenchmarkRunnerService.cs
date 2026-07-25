using Trackdub.Benchmarks;
using Trackdub.Domain;

namespace DubBench.Services;

/// <summary>
/// Thin service that delegates to Trackdub's existing benchmark runners.
/// </summary>
public interface IBenchmarkRunnerService
{
    /// <summary>
    /// Runs ONNX model inference benchmark using <see cref="Trackdub.Inference.Onnx.OnnxModelBenchmarkRunner"/>.
    /// </summary>
    Task<BenchmarkReport> RunOnnxModelBenchmarkAsync(BenchmarkRequest request, CancellationToken ct = default);

    /// <summary>
    /// Runs audio preparation benchmark using <see cref="AudioPrepBenchmarkRunner"/>.
    /// </summary>
    Task<AudioPrepBenchmarkReport> RunAudioPrepBenchmarkAsync(AudioPrepBenchmarkOptions options, CancellationToken ct = default);

    /// <summary>
    /// Runs dubbing pipeline estimate using <see cref="DubbingBenchmarkRunner"/>.
    /// </summary>
    Task<DubbingBenchmarkReport> RunDubbingBenchmarkAsync(DubbingBenchmarkOptions options, CancellationToken ct = default);
}
