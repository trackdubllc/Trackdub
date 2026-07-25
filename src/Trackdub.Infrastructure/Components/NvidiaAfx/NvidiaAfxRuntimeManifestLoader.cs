using System.Text.Json;

namespace Trackdub.Infrastructure.Components.NvidiaAfx;

public static class NvidiaAfxRuntimeManifestLoader
{
    public static NvidiaAfxRuntimeManifest Load(string manifestPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(manifestPath);
        string fullPath = Path.GetFullPath(manifestPath);
        if (!File.Exists(fullPath))
        {
            throw new FileNotFoundException("NVIDIA AFX runtime manifest file was not found.", fullPath);
        }

        using FileStream stream = File.OpenRead(fullPath);
        NvidiaAfxRuntimeManifest? manifest = JsonSerializer.Deserialize<NvidiaAfxRuntimeManifest>(
            stream,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (manifest is null || manifest.Packages is null || manifest.Packages.Length == 0)
        {
            throw new InvalidOperationException("NVIDIA AFX runtime manifest does not contain any packages.");
        }

        return manifest;
    }
}
