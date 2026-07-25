namespace Trackdub.Licensing;

/// <summary>
/// Result of license validation, including tier and diagnostic metadata.
/// </summary>
public sealed record LicenseValidationResult(
    LicenseTier Tier,
    string? LicenseId,
    int MachinesUsed,
    int MachinesMax,
    DateTimeOffset? ExpiresAt,
    string? DegradationReason,
    bool UnlimitedActivations = false);
