namespace Trackdub.Contracts.StarterPacks;

public interface ICloudCredentialReadiness
{
    Task<CloudCredentialReadinessReport> EvaluateAsync(
        StarterPackCloudDefaults cloudDefaults,
        CancellationToken cancellationToken = default);
}

public sealed record CloudCredentialReadinessReport(
    bool IsReady,
    IReadOnlyList<string> MissingProviders,
    string? BlockedReason);
