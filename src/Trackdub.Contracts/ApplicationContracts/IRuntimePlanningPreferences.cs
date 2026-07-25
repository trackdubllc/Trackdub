namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>
/// Studio-scoped inputs for runtime planning (profiler tier and benchmark evidence).
/// Implemented in Composition; consumed by Inference.Onnx and Application stage runs.
/// </summary>
public interface IRuntimePlanningPreferences
{
    /// <summary>
    /// Effective manifest tier preference (profiler override or studio default). Null when unset.
    /// </summary>
    Task<string?> GetPreferredModelTierAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Hardware profiler evidence id when a non-stale benchmark is available for planner bias. Null otherwise.
    /// </summary>
    Task<string?> GetBenchmarkEvidenceIdAsync(CancellationToken cancellationToken = default);
}
