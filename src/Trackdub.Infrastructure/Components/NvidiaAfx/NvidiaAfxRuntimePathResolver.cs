namespace Trackdub.Infrastructure.Components.NvidiaAfx;

/// <summary>
/// Resolves the on-disk NVIDIA AFX runtime root. Prefer an explicit override (settings / env),
/// then the ComponentStore install path from a verified manifest download.
/// </summary>
public static class NvidiaAfxRuntimePathResolver
{
    public const string RuntimeRootEnvironmentVariable = "TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT";
    public const string NativeLibraryFileName = "NvAudioEffects.dll";

    public static string? ResolveRuntimeRoot(
        ComponentStore componentStore,
        string? configuredRuntimeDirectory = null)
    {
        ArgumentNullException.ThrowIfNull(componentStore);

        string? fromSettings = NormalizeExistingDirectory(configuredRuntimeDirectory);
        if (fromSettings is not null)
        {
            return fromSettings;
        }

        string? fromEnvironment = NormalizeExistingDirectory(
            Environment.GetEnvironmentVariable(RuntimeRootEnvironmentVariable));
        if (fromEnvironment is not null)
        {
            return fromEnvironment;
        }

        string? installed = componentStore.GetInstallPath(NvidiaAfxRuntimeDownloader.ComponentId);
        return NormalizeExistingDirectory(installed);
    }

    public static bool HasNativeLibrary(string runtimeRoot) =>
        File.Exists(Path.Join(runtimeRoot, NativeLibraryFileName));

    private static string? NormalizeExistingDirectory(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        string fullPath = Path.GetFullPath(path.Trim());
        return Directory.Exists(fullPath) ? fullPath : null;
    }
}
