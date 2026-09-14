using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Default <see cref="IPipelineModelSetupInteraction"/> for hosts without an
/// interactive provisioning surface: auto-downloads provisionable models and
/// cancels otherwise. Registered by <c>AddTrackdub</c>; desktop hosts replace
/// it with a dialog-driven implementation.
/// </summary>
public sealed class HeadlessPipelineModelSetupInteraction : IPipelineModelSetupInteraction
{
    public RuntimeModelSetupCallbacks CreateCallbacks(
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken) =>
        HeadlessRuntimeModelSetup.CreateCallbacks(cancellationToken);
}
