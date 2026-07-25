using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// A device paired with its computed hardware score for a specific stage.
/// </summary>
public sealed record ScoredDevice(
    DeviceEntry Device,
    HardwareScore Score,
    bool IsPartialOffload = false);
