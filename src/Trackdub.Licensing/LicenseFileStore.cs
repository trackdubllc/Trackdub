namespace Trackdub.Licensing;

/// <summary>
/// Reads and writes the license token file from the platform-specific application data directory.
/// </summary>
public sealed class LicenseFileStore : ILicenseTokenStore
{
    private const string AppDirectoryName = "Trackdub";
    private const string TokenFileName = "license.jwt";
    private const string KeyFileName = "license.key";

    private readonly string? _customBasePath;

    /// <summary>
    /// Creates a new instance using the platform-specific application data directory.
    /// </summary>
    public LicenseFileStore()
    {
    }

    /// <summary>
    /// Creates a new instance using a custom base directory (for testing).
    /// </summary>
    internal LicenseFileStore(string customBasePath)
    {
        _customBasePath = customBasePath;
    }

    /// <summary>
    /// Gets the full path to the token file on the current platform.
    /// </summary>
    public string GetTokenPath()
    {
        var basePath = _customBasePath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, AppDirectoryName, TokenFileName);
    }

    private string GetKeyPath()
    {
        var basePath = _customBasePath
            ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        return Path.Combine(basePath, AppDirectoryName, KeyFileName);
    }

    /// <summary>
    /// Reads the token file. Returns null if the file does not exist.
    /// </summary>
    public string? ReadToken()
    {
        var path = GetTokenPath();
        if (!File.Exists(path))
            return null;

        return File.ReadAllText(path).Trim();
    }

    /// <summary>
    /// Writes the token to disk. Creates the directory if it does not exist.
    /// </summary>
    public void WriteToken(string token)
    {
        var path = GetTokenPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, token);
    }

    /// <summary>
    /// Persists the customer license key used at activation (needed for /deactivate seat release).
    /// </summary>
    public void WriteLicenseKey(string licenseKey)
    {
        var path = GetKeyPath();
        var directory = Path.GetDirectoryName(path)!;
        Directory.CreateDirectory(directory);
        File.WriteAllText(path, licenseKey.Trim());
    }

    /// <summary>
    /// Reads the stored license key, or null if none.
    /// </summary>
    public string? ReadLicenseKey()
    {
        var path = GetKeyPath();
        if (!File.Exists(path))
            return null;

        return File.ReadAllText(path).Trim();
    }

    /// <summary>
    /// Deletes the token and license-key files if they exist.
    /// </summary>
    public void DeleteToken()
    {
        var path = GetTokenPath();
        if (File.Exists(path))
            File.Delete(path);

        var keyPath = GetKeyPath();
        if (File.Exists(keyPath))
            File.Delete(keyPath);
    }
}
