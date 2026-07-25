using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Scores and ranks available compute devices for a given pipeline stage,
/// producing a deterministic ordering that the runtime planner uses for
/// device selection and fallback.
/// </summary>
public interface IHardwareMatrix
{
    /// <summary>
    /// Produces a ranked list of scored devices for the given stage.
    /// Devices excluded by provider constraints, memory limits, or run-level
    /// exclusions are omitted from the result.
    /// </summary>
    IReadOnlyList<ScoredDevice> RankDevices(
        RuntimeStage stage,
        IReadOnlyList<DeviceEntry> devices,
        AffinityRule? affinityRule = null,
        DeviceExclusionSet? exclusions = null,
        int? peakVramMb = null,
        int? minVramMb = null);
}
