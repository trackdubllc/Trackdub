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

        // The Maxine 2.x SDK root keeps the core DLL in bin/ beside features/; flat installs and
        // staged packages keep it at the root.
        string[] searchDirectories = [runtimeRoot, Path.Join(runtimeRoot, "bin")];

        string? existing = searchDirectories
            .SelectMany(directory => NativeLibraryFileNames.Select(fileName => Path.Join(directory, fileName)))
            .FirstOrDefault(File.Exists);
        if (existing is not null)
        {
            return existing;
        }

        // Case-insensitive fallback for Linux-hosted fixtures / wine-style trees.
        return searchDirectories
            .Where(Directory.Exists)
            .SelectMany(directory => Directory.EnumerateFiles(directory, "*.dll"))
            .FirstOrDefault(path =>
                NativeLibraryFileNames.Any(expected =>
                    string.Equals(expected, Path.GetFileName(path), StringComparison.OrdinalIgnoreCase)));
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
    /// True when the tree looks like a Maxine 3.x SDK root (<c>features/</c> present).
    /// Legacy flat <c>models/</c>-only fixtures skip feature-DLL presence checks.
    /// </summary>
    public static bool HasFeaturesDirectory(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        return Directory.Exists(Path.Join(runtimeRoot, "features"));
    }

    /// <summary>
    /// Resolves a Maxine feature DLL under <c>features/&lt;folder&gt;/bin/</c>, or next to the core DLL.
    /// </summary>
    public static string? ResolveFeatureNativeLibraryPath(string runtimeRoot, string featureFolder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(featureFolder);

        string folder = featureFolder.Trim().TrimStart('/', '\\');
        string relative = FeatureNativeLibraryRelativePath(folder);
        string primary = Path.Join(runtimeRoot, relative);
        if (File.Exists(primary))
        {
            return primary;
        }

        // Maxine docs also allow the feature DLL beside the core library.
        string sidecar = Path.Join(runtimeRoot, folder + ".dll");
        if (File.Exists(sidecar))
        {
            return sidecar;
        }

        if (!Directory.Exists(runtimeRoot))
        {
            return null;
        }

        string expectedName = folder + ".dll";
        foreach (string path in Directory.EnumerateFiles(runtimeRoot, "*.dll"))
        {
            if (string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
            {
                return path;
            }
        }

        string featureBin = Path.Join(runtimeRoot, "features", folder, "bin");
        if (Directory.Exists(featureBin))
        {
            foreach (string path in Directory.EnumerateFiles(featureBin, "*.dll"))
            {
                if (string.Equals(Path.GetFileName(path), expectedName, StringComparison.OrdinalIgnoreCase))
                {
                    return path;
                }
            }
        }

        return null;
    }

    public static bool HasFeatureNativeLibrary(string runtimeRoot, string featureFolder) =>
        ResolveFeatureNativeLibraryPath(runtimeRoot, featureFolder) is not null;

    public static bool HasRequiredFeatureLibraries(
        string runtimeRoot,
        IEnumerable<string> featureFolders)
    {
        ArgumentNullException.ThrowIfNull(featureFolders);
        return featureFolders.All(folder => HasFeatureNativeLibrary(runtimeRoot, folder));
    }

    /// <summary>
    /// Enumerates the third-party DLLs (CUDA, TensorRT, OpenSSL) the Maxine 2.x SDK keeps under
    /// <c>bin/external/*/bin</c>. Feature DLLs depend on them but those folders are not on the
    /// default DLL search path, so they must be preloaded by absolute path.
    /// </summary>
    public static IEnumerable<string> EnumerateExternalDependencyPaths(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        foreach (string externalRoot in new[]
                 {
                     Path.Join(runtimeRoot, "bin", "external"),
                     Path.Join(runtimeRoot, "external"),
                 })
        {
            if (!Directory.Exists(externalRoot))
            {
                continue;
            }

            foreach (string dllPath in Directory.EnumerateFiles(externalRoot, "*.dll", SearchOption.AllDirectories))
            {
                yield return dllPath;
            }
        }
    }

    /// <summary>
    /// Enumerates Maxine feature native libraries under <c>features/*/bin/*.dll</c> plus
    /// sidecar <c>nvafx*.dll</c> next to the core library (for managed preload).
    /// </summary>
    public static IEnumerable<string> EnumerateFeatureNativeLibraryPaths(string runtimeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimeRoot);

        string featuresRoot = Path.Join(runtimeRoot, "features");
        if (Directory.Exists(featuresRoot))
        {
            IEnumerable<string> featureBinDirectories = Directory
                .EnumerateDirectories(featuresRoot)
                .Select(featureDirectory => Path.Join(featureDirectory, "bin"))
                .Where(Directory.Exists);

            foreach (string binDirectory in featureBinDirectories)
            {
                foreach (string dllPath in Directory.EnumerateFiles(binDirectory, "*.dll"))
                {
                    yield return dllPath;
                }
            }
        }

        if (!Directory.Exists(runtimeRoot))
        {
            yield break;
        }

        foreach (string sidecar in Directory.EnumerateFiles(runtimeRoot, "nvafx*.dll"))
        {
            yield return sidecar;
        }
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

        return EnumerateModelCandidates(runtimeRoot, featureFolder, modelStem, architectureBucket)
            .FirstOrDefault(File.Exists);
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
