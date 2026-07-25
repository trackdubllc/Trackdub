using Trackdub.Application.Transcripts;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Centralizes stage-key to model provisioning for UI resolve and automation paths.
/// Evaluation remains on <see cref="IPipelineReadinessService"/>.
/// </summary>
public interface IStageReadinessOrchestrator
{
    Task<RuntimeModelSetupResult> ProvisionStageAsync(
        StageReadinessProvisionRequest request,
        CancellationToken cancellationToken = default);
}
