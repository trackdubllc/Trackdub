namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Normalized score (0.0–1.0) for a (stage, device) pair with factor breakdown.
/// </summary>
public sealed record HardwareScore(
    double TotalScore,
    double ThroughputFactor,
    double MemoryHeadroomFactor,
    double LatencyBonus);
