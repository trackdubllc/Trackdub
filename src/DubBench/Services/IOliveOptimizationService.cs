namespace DubBench.Services;

/// <summary>
/// Service that invokes the Olive optimization tooling as a subprocess
/// to optimize ONNX models before benchmarking.
/// </summary>
public interface IOliveOptimizationService
{
    /// <summary>Whether Olive is available on this system.</summary>
    bool IsOliveAvailable { get; }

    /// <summary>
    /// Run Olive optimization on the given model.
    /// Returns the path to the optimized model, or null if optimization failed.
    /// </summary>
    Task<string?> OptimizeAsync(
        string modelPath,
        string outputDir,
        string? provider = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Check if Olive is installed and accessible.
    /// </summary>
    Task<bool> ProbeAvailabilityAsync(CancellationToken cancellationToken = default);
}
