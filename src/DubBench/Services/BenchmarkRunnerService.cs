using Trackdub.Benchmarks;
using Trackdub.Domain;
#if WINDOWS
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
#endif

namespace DubBench.Services;

/// <summary>
/// Thin delegation wrapper around Trackdub's existing benchmark runners.
/// VMs instantiate this directly (no DI container for MVP).
/// </summary>
public sealed class BenchmarkRunnerService : IBenchmarkRunnerService
{
#if WINDOWS
    private readonly IModelBenchmarkRunner? _onnxRunner;
#endif
    private readonly AudioPrepBenchmarkRunner _audioRunner;
    private readonly DubbingBenchmarkRunner _dubbingRunner;

    public BenchmarkRunnerService()
    {
#if WINDOWS
        _onnxRunner = BenchmarkOnnxExecutionBootstrap.CreateOnnxRunner();
#endif
        _audioRunner = new AudioPrepBenchmarkRunner();
        _dubbingRunner = new DubbingBenchmarkRunner();
    }

    /// <inheritdoc />
    public async Task<BenchmarkReport> RunOnnxModelBenchmarkAsync(
        BenchmarkRequest request,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
#if WINDOWS
        BenchmarkOnnxExecutionBootstrap.ConfigureExecution(request.WindowsMlDevicePolicyKey);
        if (_onnxRunner is null)
        {
            return new BenchmarkReport(
                Scenario: "onnx-model",
                ModelPath: request.ModelPath ?? string.Empty,
                ReportPath: request.ReportPath ?? string.Empty,
                Status: BenchmarkStatus.Failed,
                RequestedProvider: request.ProviderPreference.ToString(),
                SelectedProvider: "None",
                RunCount: request.RunCount,
                SupportsExecution: false,
                ModelSizeBytes: 0,
                Measurements: new BenchmarkMeasurements(null, null, null, null, null, null, null),
                FailureReason: "ONNX model benchmark runner is unavailable on this Windows host.",
                Notes: [],
                GeneratedAtUtc: DateTimeOffset.UtcNow);
        }

        return await _onnxRunner.RunAsync(request, ct).ConfigureAwait(false);
#else
        await Task.CompletedTask.ConfigureAwait(false);
        return new BenchmarkReport(
            Scenario: "onnx-model",
            ModelPath: request.ModelPath ?? string.Empty,
            ReportPath: request.ReportPath ?? string.Empty,
            Status: BenchmarkStatus.Failed,
            RequestedProvider: request.ProviderPreference.ToString(),
            SelectedProvider: "None",
            RunCount: request.RunCount,
            SupportsExecution: false,
            ModelSizeBytes: 0,
            Measurements: new BenchmarkMeasurements(null, null, null, null, null, null, null),
            FailureReason: "ONNX model benchmarks require Windows (Windows ML / ONNX Runtime EP support).",
            Notes: [],
            GeneratedAtUtc: DateTimeOffset.UtcNow);
#endif
    }

    /// <inheritdoc />
    public async Task<AudioPrepBenchmarkReport> RunAudioPrepBenchmarkAsync(
        AudioPrepBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await _audioRunner.RunAsync(options, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<DubbingBenchmarkReport> RunDubbingBenchmarkAsync(
        DubbingBenchmarkOptions options,
        CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        return await _dubbingRunner.RunAsync(options, ct).ConfigureAwait(false);
    }
}
