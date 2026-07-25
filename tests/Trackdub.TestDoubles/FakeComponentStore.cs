namespace Trackdub.TestDoubles;

/// <summary>
/// In-memory test double for component store operations.
/// Tracks installed components for testing download/install/uninstall flows.
/// </summary>
public sealed class FakeComponentStore
{
    private readonly Dictionary<string, ComponentEntry> _components = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns whether the specified component is currently installed.
    /// </summary>
    public bool IsInstalled(string componentId)
    {
        IsInstalledCallCount++;
        return _components.TryGetValue(componentId, out var entry) && entry.IsInstalled;
    }

    /// <summary>
    /// Returns the install path for the specified component, or null if not installed.
    /// </summary>
    public string? GetInstallPath(string componentId) =>
        _components.TryGetValue(componentId, out var entry) && entry.IsInstalled
            ? entry.InstallPath
            : null;

    /// <summary>
    /// Marks a component as installed with the given path.
    /// </summary>
    public void MarkInstalled(string componentId, string installPath)
    {
        _components[componentId] = new ComponentEntry(IsInstalled: true, InstallPath: installPath);
    }

    /// <summary>
    /// Marks a component as uninstalled, removing it from the store.
    /// </summary>
    public void MarkUninstalled(string componentId)
    {
        _components.Remove(componentId);
    }

    /// <summary>
    /// Returns all currently installed component identifiers.
    /// </summary>
    public IReadOnlyList<string> GetInstalledComponents() =>
        _components
            .Where(kvp => kvp.Value.IsInstalled)
            .Select(kvp => kvp.Key)
            .ToList();

    /// <summary>
    /// Number of times <see cref="IsInstalled"/> has been called.
    /// Useful for verifying caching behavior in tests.
    /// </summary>
    public int IsInstalledCallCount { get; private set; }

    private sealed record ComponentEntry(bool IsInstalled, string? InstallPath);
}
