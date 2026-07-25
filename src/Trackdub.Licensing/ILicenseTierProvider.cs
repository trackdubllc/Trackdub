namespace Trackdub.Licensing;

/// <summary>
/// Synchronous read-only access to the resolved tier. Thread-safe after initialization.
/// </summary>
public interface ILicenseTierProvider
{
    LicenseTier CurrentTier { get; }
    LicenseValidationResult ValidationResult { get; }
}
