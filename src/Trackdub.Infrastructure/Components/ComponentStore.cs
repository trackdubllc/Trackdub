using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Components;

/// <summary>
/// Manages the local directory where optional downloadable runtime components
/// (such as the OpenVINO runtime) are stored, loaded from, and managed.
/// </summary>
public sealed class ComponentStore
{
    private const string InstallMarkerFileName = ".component-installed";
    private readonly string _componentsRoot;
    private readonly IApplicationLogger _logger;

    public ComponentStore(string componentsRoot, IApplicationLogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentsRoot);
        ArgumentNullException.ThrowIfNull(logger);

        _componentsRoot = Path.GetFullPath(componentsRoot);
        _logger = logger;
    }

    /// <summary>
    /// Returns the root directory for all components.
    /// </summary>
    public string RootDirectory => _componentsRoot;

    /// <summary>
    /// Returns whether the specified component is currently installed.
    /// </summary>
    public bool IsInstalled(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        string componentPath = GetComponentDirectory(componentId);
        if (!Directory.Exists(componentPath))
        {
            return false;
        }

        string markerPath = GetInstallMarkerPath(componentId);
        if (File.Exists(markerPath))
        {
            return true;
        }

        try
        {
            bool hasAnyFiles = Directory.EnumerateFiles(componentPath, "*", SearchOption.AllDirectories).Any();
            if (hasAnyFiles)
            {
                _logger.LogInformation(
                    $"Component '{componentId}' contains files but no install marker '{InstallMarkerFileName}'. " +
                    "Writing marker to migrate legacy install state.");
                MarkInstalled(componentId);
                return true;
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to inspect component '{componentId}' installation state.", ex);
            return false;
        }
    }

    /// <summary>
    /// Returns the install path for the specified component, or null if not installed.
    /// </summary>
    public string? GetInstallPath(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        string componentPath = GetComponentDirectory(componentId);
        return IsInstalled(componentId) ? componentPath : null;
    }

    /// <summary>
    /// Returns the directory path where a component would be installed.
    /// </summary>
    public string GetComponentDirectory(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        return Path.Combine(_componentsRoot, componentId);
    }

    /// <summary>
    /// Removes all files for the specified component from the store.
    /// </summary>
    public void Remove(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        string componentPath = GetComponentDirectory(componentId);

        if (Directory.Exists(componentPath))
        {
            try
            {
                Directory.Delete(componentPath, recursive: true);
                _logger.LogInformation($"Component '{componentId}' removed from store.");
            }
            catch (Exception ex)
            {
                _logger.LogError($"Failed to remove component '{componentId}' from store.", ex);
                throw;
            }
        }
    }

    /// <summary>
    /// Ensures the component directory exists and returns its path.
    /// </summary>
    public string EnsureComponentDirectory(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        string componentPath = GetComponentDirectory(componentId);
        Directory.CreateDirectory(componentPath);
        return componentPath;
    }

    public void MarkInstalled(string componentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(componentId);
        string componentPath = EnsureComponentDirectory(componentId);
        string markerPath = Path.Combine(componentPath, InstallMarkerFileName);
        string tempMarkerPath = Path.Combine(componentPath, $"{InstallMarkerFileName}.{Guid.NewGuid():N}.tmp");

        try
        {
            File.WriteAllText(tempMarkerPath, DateTimeOffset.UtcNow.ToString("O"));
            File.Move(tempMarkerPath, markerPath, overwrite: true);
        }
        catch (Exception ex)
        {
            _logger.LogWarning($"Failed to write install marker for component '{componentId}'.", ex);

            try
            {
                if (File.Exists(tempMarkerPath))
                {
                    File.Delete(tempMarkerPath);
                }
            }
            catch
            {
                // Best-effort cleanup only; marker write failures should not throw.
            }
        }
    }

    private string GetInstallMarkerPath(string componentId)
    {
        return Path.Combine(GetComponentDirectory(componentId), InstallMarkerFileName);
    }
}
