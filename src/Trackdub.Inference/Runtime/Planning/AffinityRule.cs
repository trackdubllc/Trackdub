using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Declarative mapping from a stage to a preferred device.
/// </summary>
public sealed record AffinityRule(
    RuntimeStage Stage,
    DeviceKind PreferredKind,
    int? PreferredDeviceIndex = null);
