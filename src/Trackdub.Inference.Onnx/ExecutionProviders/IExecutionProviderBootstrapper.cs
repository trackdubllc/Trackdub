using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.ExecutionProviders;

/// <summary>
/// Platform-specific execution provider bootstrapper.
/// Handles provider registration, capability detection, and fallback logic.
/// </summary>
public interface IExecutionProviderBootstrapper
{
    /// <summary>
    /// Register or validate an execution provider for use in inference sessions.
    /// </summary>
    /// <param name="provider">The execution provider kind to bootstrap.</param>
    /// <param name="allowDownloads">Whether to allow provider downloads (e.g., Windows ML catalog downloads).</param>
    /// <param name="cancellationToken">Cancellation token.</param>
    /// <returns>Bootstrap result with provider selection and status.</returns>
    Task<ExecutionProviderBootstrapResult> BootstrapAsync(
        ExecutionProviderKind provider,
        bool allowDownloads,
        CancellationToken cancellationToken);

    /// <summary>
    /// Check provider readiness without caching (used for diagnostics).
    /// </summary>
    Task<ExecutionProviderBootstrapResult> CheckReadinessAsync(
        ExecutionProviderKind provider,
        CancellationToken cancellationToken);
}
