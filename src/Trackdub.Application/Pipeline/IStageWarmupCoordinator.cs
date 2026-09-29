using Trackdub.Application.Transcripts;
using Trackdub.Domain;

namespace Trackdub.Application.Pipeline;

/// <summary>
/// Optimization-only stage warmup request. Carries the same runtime model selections the
/// stage will execute with so the warmed model/provider/profile matches the run.
/// </summary>
public sealed record StageWarmupRequest(
    RuntimeStage Stage,
    RuntimeModelSelections Selections,
    string? SourceLanguageCode = null,
    string? TargetLanguageCode = null,
    bool RequiresVoiceClone = false);

/// <summary>
/// Optimization result only. <see cref="Succeeded"/> does not imply stage readiness —
/// actual stage planning/execution remains the authority.
/// </summary>
public sealed record StageWarmupResult(
    bool Attempted,
    bool Succeeded,
    string? Detail = null);

/// <summary>
/// Warms the selected model/provider for an imminent runtime stage while its preceding
/// stage executes. Implementations must not create stage-run records, artifacts, readiness
/// success, or progress events; failures are optimization-only.
/// </summary>
public interface IStageWarmupCoordinator
{
    Task<StageWarmupResult> WarmAsync(
        StageWarmupRequest request,
        CancellationToken cancellationToken = default);
}
