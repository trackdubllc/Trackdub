using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Host-provided interaction surface for runtime model provisioning during
/// pipeline pre-flight and stage-level model checks.
/// <para>
/// Headless hosts register an implementation that auto-downloads provisionable
/// models and cancels otherwise. Interactive hosts (desktop) register an
/// implementation that surfaces download/import prompts on the UI thread.
/// </para>
/// <para>
/// Resolved from the session service scope. When no implementation is
/// registered, callers fall back to <see cref="HeadlessRuntimeModelSetup"/>
/// defaults.
/// </para>
/// </summary>
public interface IPipelineModelSetupInteraction
{
    /// <summary>
    /// Builds the callback set the provisioning workflow invokes for decisions,
    /// import-file picking, download progress, and running long operations.
    /// </summary>
    /// <param name="progress">Pipeline progress sink for status messages surfaced during setup.</param>
    /// <param name="cancellationToken">Token governing the setup operations.</param>
    RuntimeModelSetupCallbacks CreateCallbacks(
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken);
}
