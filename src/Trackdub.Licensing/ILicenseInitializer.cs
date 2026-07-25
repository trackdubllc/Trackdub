namespace Trackdub.Licensing;

/// <summary>
/// Initializes the license system — validates token, resolves tier.
/// Called once at app startup.
/// </summary>
public interface ILicenseInitializer
{
    Task<LicenseValidationResult> InitializeAsync(CancellationToken cancellationToken = default);
}
