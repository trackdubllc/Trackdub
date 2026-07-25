namespace Trackdub.Licensing;

/// <summary>
/// Public interface for token persistence — allows writing and deleting tokens from external layers (e.g., UI activation).
/// </summary>
public interface ILicenseTokenStore
{
    /// <summary>
    /// Writes the token to the platform-specific token file.
    /// </summary>
    void WriteToken(string token);

    /// <summary>
    /// Deletes the token file if it exists.
    /// </summary>
    void DeleteToken();

    /// <summary>
    /// Reads the token file. Returns null if the file does not exist.
    /// </summary>
    string? ReadToken();

    /// <summary>
    /// Persists the customer-facing license key used at activation (for server deactivate).
    /// </summary>
    void WriteLicenseKey(string licenseKey);

    /// <summary>
    /// Reads the stored license key, or null if none.
    /// </summary>
    string? ReadLicenseKey();
}
