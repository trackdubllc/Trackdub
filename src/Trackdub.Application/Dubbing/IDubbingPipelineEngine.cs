using Trackdub.Contracts.Pipeline;

using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Abstraction over a dubbing pipeline engine for headless and batch execution.
/// </summary>
public interface IDubbingPipelineEngine
{
    /// <summary>
    /// Executes the dubbing pipeline according to the provided options.
    /// </summary>
    Task<DubbingRunResult> ExecuteAsync(
        DubbingSessionOptions options,
        IProgress<PipelineProgressEvent>? progress = null,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Additive observer contract for engines that surface transient-fault
/// telemetry to callers. The base <see cref="IDubbingPipelineEngine"/>
/// preserves its existing surface for SDK consumers that don't yet consume
/// fault streams; consumers opt in via this sibling interface. See
/// <c>docs/internal/pipeline-readiness-spec.md</c> section 4.4 + 11.3.
/// </summary>
public interface ITransientFaultReporting
{
    /// <summary>
    /// Streams transient-fault records emitted during this engine's lifetime
    /// as a cold <see cref="IAsyncEnumerable{T}"/>. Enumeration starts with
    /// the current snapshot in arrival order; live updates follow if the
    /// implementation supports it.
    /// </summary>
    IAsyncEnumerable<PipelineTransientFault> TransientFaultsAsync(CancellationToken cancellationToken = default);
}
