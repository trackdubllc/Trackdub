using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Describes a pipeline stage's workload characteristics for hardware scoring.
/// </summary>
public sealed record StageWorkloadProfile(
    RuntimeStage Stage,
    int ModelSizeMb,
    LatencySensitivity LatencySensitivity,
    int PeakMemoryMb);
