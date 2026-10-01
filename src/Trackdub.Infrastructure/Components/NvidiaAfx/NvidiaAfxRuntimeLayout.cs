namespace Trackdub.Infrastructure.Components.NvidiaAfx;

/// <summary>
/// Maxine AFX Windows SDK 3.x on-disk layout helpers (core DLL + <c>features/</c> AI packages).
/// See NVIDIA Install / Create Effect / Set Parameters docs. Also accepts a legacy flat
/// <c>models/*.nvam</c> tree for older staged packages.
/// </summary>
public static class NvidiaAfxRuntimeLayout
{
    /// <summary>Maxine docs use <c>NVAudioEffects.dll</c>; older Trackdub paths used <c>NvAudioEffects.dll</c>.</summary>
    public static readonly string[] NativeLibraryFileNames =
    [
        "NVAudioEffects.dll",
        "NvAudioEffects.dll",
    ];

    public static readonly string[] ModelFileExtensions =
    [
        ".trtpkg",
        ".nvam",
        ".onnx",
        "",
    ];

    public static readonly string[] ArchitectureBuckets =
    [
        "turing",
        "ampere",
        "ada",
        "blackwell",
    ];

    public static string? ResolveNativeLibraryPath(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        foreach (string fileName in NativeLibraryFileNames)
        {
            string candidate = Path.Join(runtimeRoot, fileName);
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        // Case-insensitive fallback for Linux-hosted fixtures / wine-style trees.
        if (!Directory.Exists(runtimeRoot))
        {
            return null;
        }

        foreach (string path in Directory.EnumerateFiles(runtimeRoot, "*.dll"))
        {
            string name = Path.GetFileName(path);
            if (NativeLibraryFileNames.Any(expected =>
                    string.Equals(expected, name, StringComparison.OrdinalIgnoreCase)))
            {
                return path;
            }
        }

        return null;
    }

    public static bool HasNativeLibrary(string runtimeRoot) =>
        ResolveNativeLibraryPath(runtimeRoot) is not null;

    /// <summary>
    /// Feature DLL Maxine loads on Windows when creating an effect
    /// (<c>features\nvafx&lt;effect&gt;\bin\nvafx&lt;effect&gt;.dll</c>).
    /// </summary>
    public static string FeatureNativeLibraryRelativePath(string featureFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(featureFolder);
        string folder = featureFolder.Trim().TrimStart('/', '\\');
        string dllName = folder + ".dll";
        return Path.Join("features", folder, "bin", dllName);
    }

    /// <summary>
    /// Resolves a model file for <paramref name="modelStem"/> under Maxine 3.x and legacy layouts.
    /// </summary>
    public static string? ResolveModelFile(
        string runtimeRoot,
        string featureFolder,
        string modelStem,
        string? architectureBucket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelStem);

        foreach (string candidate in EnumerateModelCandidates(runtimeRoot, featureFolder, modelStem, architectureBucket))
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    public static bool HasRequiredModels(
        string runtimeRoot,
        IEnumerable<(string FeatureFolder, string ModelStem)> models,
        string? architectureBucket = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        return models.All(model =>
            ResolveModelFile(runtimeRoot, model.FeatureFolder, model.ModelStem, architectureBucket) is not null);
    }

    public static IEnumerable<string> EnumerateModelCandidates(
        string runtimeRoot,
        string featureFolder,
        string modelStem,
        string? architectureBucket = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelStem);

        string feature = featureFolder.Trim().TrimStart('/', '\\');
        string stem = modelStem.Trim();
        string[] archOrder = BuildArchitectureSearchOrder(architectureBucket);

        foreach (string arch in archOrder)
        {
            // Maxine 3.x: features/<feature>/models/<arch>/<stem>.*
            foreach (string path in WithExtensions(Path.Join(runtimeRoot, "features", feature, "models", arch, stem)))
            {
                yield return path;
            }

            // download_features.ps1 may nest under features/<arch>/<feature>/models/
            foreach (string path in WithExtensions(Path.Join(runtimeRoot, "features", arch, feature, "models", stem)))
            {
                yield return path;
            }

            foreach (string path in WithExtensions(Path.Join(runtimeRoot, "features", arch, feature, "models", arch, stem)))
            {
                yield return path;
            }

            // Root models/<arch>/<stem>.* (SDK-wide models tree)
            foreach (string path in WithExtensions(Path.Join(runtimeRoot, "models", arch, stem)))
            {
                yield return path;
            }
        }

        // Feature models without arch folder
        foreach (string path in WithExtensions(Path.Join(runtimeRoot, "features", feature, "models", stem)))
        {
            yield return path;
        }

        // Legacy Trackdub flat models/<stem>.nvam (and other extensions)
        foreach (string path in WithExtensions(Path.Join(runtimeRoot, "models", stem)))
        {
            yield return path;
        }
    }

    private static string[] BuildArchitectureSearchOrder(string? architectureBucket)
    {
        if (string.IsNullOrWhiteSpace(architectureBucket))
        {
            return ArchitectureBuckets;
        }

        string preferred = architectureBucket.Trim().ToLowerInvariant();
        return
        [
            preferred,
            .. ArchitectureBuckets.Where(candidate =>
                !string.Equals(candidate, preferred, StringComparison.OrdinalIgnoreCase)),
        ];
    }

    private static IEnumerable<string> WithExtensions(string pathWithoutExtension)
    {
        foreach (string extension in ModelFileExtensions)
        {
            yield return string.IsNullOrEmpty(extension)
                ? pathWithoutExtension
                : pathWithoutExtension + extension;
        }
    }
}
