using Trackdub.Inference.Runtime.ModelManifest;

namespace Trackdub.Inference.Onnx;

public sealed class BenchmarkModelPathResolver(
    BundledModelManifestRegistry? manifestRegistry = null,
    string? modelCacheDirectory = null)
{
    private readonly string? _modelCacheDirectory = string.IsNullOrWhiteSpace(modelCacheDirectory)
        ? null
        : Path.GetFullPath(modelCacheDirectory);
    private static readonly string[] PreferredEntryFileNames =
    [
        "silero_vad.onnx",
        "encoder.onnx",
        "encoder_model.onnx",
        "model.onnx",
        "speech_encoder.onnx"
    ];

    public static BenchmarkModelPathResolver CreateDefault()
    {
        BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out _);
        return new BenchmarkModelPathResolver(registry, ResolveDefaultModelCacheDirectory());
    }

    private static string? ResolveDefaultModelCacheDirectory()
    {
        string? configured = Environment.GetEnvironmentVariable("TRACKDUB_MODEL_CACHE");
        if (!string.IsNullOrWhiteSpace(configured))
        {
            return configured;
        }

        string? localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localAppData))
        {
            return null;
        }

        return Path.Combine(localAppData, "Trackdub", "model-cache");
    }

    public BenchmarkModelResolutionResult Discover(string reference)
    {
        if (string.IsNullOrWhiteSpace(reference))
        {
            return new BenchmarkModelResolutionResult(reference, "empty:", [], null);
        }

        string trimmedReference = reference.Trim();
        if (TryDiscoverManifestCandidates(trimmedReference, out BenchmarkModelResolutionResult? manifestResult))
        {
            return manifestResult!;
        }

        string candidatePath = ExpandToAbsolutePath(trimmedReference);
        if (File.Exists(candidatePath))
        {
            return CreateSingleCandidateResult(
                trimmedReference,
                $"file:{candidatePath}",
                new BenchmarkModelCandidate(
                    CandidateKey: $"file:{candidatePath}",
                    DisplayName: Path.GetFileNameWithoutExtension(candidatePath),
                    ModelPath: candidatePath,
                    VariantAlias: null,
                    ResolutionNote: $"Resolved model file '{trimmedReference}' to '{candidatePath}'."));
        }

        if (Directory.Exists(candidatePath))
        {
            return DiscoverFromDirectory(trimmedReference, candidatePath);
        }

        foreach (string modelStore in DiscoverModelStores())
        {
            string scopedCandidate = ExpandToAbsolutePath(Path.Combine(modelStore, trimmedReference));
            if (File.Exists(scopedCandidate))
            {
                return CreateSingleCandidateResult(
                    trimmedReference,
                    $"file:{scopedCandidate}",
                    new BenchmarkModelCandidate(
                        CandidateKey: $"file:{scopedCandidate}",
                        DisplayName: Path.GetFileNameWithoutExtension(scopedCandidate),
                        ModelPath: scopedCandidate,
                        VariantAlias: null,
                        ResolutionNote: $"Resolved model scope '{trimmedReference}' within model store '{modelStore}'."));
            }

            if (Directory.Exists(scopedCandidate))
            {
                return DiscoverFromDirectory(trimmedReference, scopedCandidate);
            }

            string[] aliasDirectories = FindScopedDirectories(modelStore, trimmedReference)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();

            if (aliasDirectories.Length == 1)
            {
                return DiscoverFromDirectory(trimmedReference, aliasDirectories[0]);
            }

            if (aliasDirectories.Length > 1)
            {
                string[] candidates = aliasDirectories
                    .Select(directory => Path.GetRelativePath(modelStore, directory))
                    .ToArray();

                return new BenchmarkModelResolutionResult(
                    trimmedReference,
                    $"alias:{trimmedReference}",
                    [],
                    null,
                    $"Model scope '{trimmedReference}' is ambiguous within '{modelStore}'. Specify one of: {string.Join(", ", candidates)}.");
            }
        }

        return new BenchmarkModelResolutionResult(
            trimmedReference,
            $"missing:{trimmedReference}",
            [],
            null,
            "Model path or scope did not resolve to an ONNX model.");
    }

    public BenchmarkModelCandidate ResolveSingle(
        string reference,
        string? explicitVariantAlias = null,
        string? preferredCandidateKey = null)
    {
        if (string.IsNullOrWhiteSpace(explicitVariantAlias))
        {
            int embeddedVariantSeparatorIndex = reference.IndexOf('@');
            if (embeddedVariantSeparatorIndex >= 0 && embeddedVariantSeparatorIndex < reference.Length - 1)
            {
                explicitVariantAlias = reference[(embeddedVariantSeparatorIndex + 1)..];
            }
        }

        BenchmarkModelResolutionResult discovery = Discover(reference);
        if (!string.IsNullOrWhiteSpace(discovery.Error))
        {
            throw new FileNotFoundException(discovery.Error, reference);
        }

        if (!string.IsNullOrWhiteSpace(explicitVariantAlias))
        {
            BenchmarkModelCandidate? explicitVariant = discovery.Candidates.FirstOrDefault(
                candidate => VariantMatches(candidate, explicitVariantAlias));

            if (explicitVariant is null)
            {
                throw new FileNotFoundException(
                    $"Model reference '{reference}' does not define variant '{explicitVariantAlias}'.",
                    reference);
            }

            return explicitVariant;
        }

        if (!string.IsNullOrWhiteSpace(preferredCandidateKey))
        {
            BenchmarkModelCandidate? preferredCandidate = discovery.Candidates.FirstOrDefault(
                candidate => candidate.CandidateKey.Equals(preferredCandidateKey, StringComparison.OrdinalIgnoreCase));

            if (preferredCandidate is not null)
            {
                return preferredCandidate;
            }
        }

        if (!string.IsNullOrWhiteSpace(discovery.DefaultCandidateKey))
        {
            BenchmarkModelCandidate? defaultCandidate = discovery.Candidates.FirstOrDefault(
                candidate => candidate.CandidateKey.Equals(discovery.DefaultCandidateKey, StringComparison.OrdinalIgnoreCase));

            if (defaultCandidate is not null)
            {
                return defaultCandidate;
            }
        }

        if (discovery.Candidates.Count == 1)
        {
            return discovery.Candidates[0];
        }

        if (discovery.Candidates.Count > 1)
        {
            throw new FileNotFoundException(
                $"Model reference '{reference}' is ambiguous. Specify --variant or choose one of: {string.Join(", ", discovery.Candidates.Select(candidate => candidate.DisplayName))}.",
                reference);
        }

        throw new FileNotFoundException("Model path or scope did not resolve to an ONNX model.", reference);
    }

    private bool TryDiscoverManifestCandidates(string reference, out BenchmarkModelResolutionResult? result)
    {
        result = null;
        if (manifestRegistry is null)
        {
            return false;
        }

        string alias = reference;
        int variantSeparatorIndex = reference.IndexOf('@');
        if (variantSeparatorIndex >= 0)
        {
            alias = reference[..variantSeparatorIndex];
        }

        if (!manifestRegistry.TryResolve(alias, out BundledModelManifestResolution? defaultResolution))
        {
            return false;
        }

        var candidates = new List<BenchmarkModelCandidate>();
        if (File.Exists(defaultResolution!.Entry.DefaultBenchmarkEntryPath))
        {
            candidates.Add(new BenchmarkModelCandidate(
                CandidateKey: "variant:default",
                DisplayName: $"{alias} (default)",
                ModelPath: defaultResolution.Entry.DefaultBenchmarkEntryPath,
                VariantAlias: "default",
                ResolutionNote: $"Resolved model alias '{reference}' using manifest '{manifestRegistry.ManifestPath}'.",
                RootDirectory: defaultResolution.Entry.RootDirectory));
        }

        foreach (BundledModelManifestVariant variant in defaultResolution.Entry.Variants)
        {
            if (!File.Exists(variant.EntryPath))
            {
                continue;
            }

            candidates.Add(new BenchmarkModelCandidate(
                CandidateKey: $"variant:{variant.Alias}",
                DisplayName: $"{alias}@{variant.Alias}",
                ModelPath: variant.EntryPath,
                VariantAlias: variant.Alias,
                ResolutionNote: $"Resolved model alias '{reference}' using manifest '{manifestRegistry.ManifestPath}'.",
                RootDirectory: defaultResolution.Entry.RootDirectory));
        }

        if (candidates.Count == 0)
        {
            BenchmarkModelResolutionResult? cacheResult = TryDiscoverCachedManifestCandidates(
                reference,
                alias,
                defaultResolution!.Entry);
            if (cacheResult is not null)
            {
                result = cacheResult;
                return true;
            }

            result = new BenchmarkModelResolutionResult(
                reference,
                $"manifest:{alias}",
                [],
                null,
                $"Model alias '{alias}' is declared in the bundled manifest, but no ONNX entry point exists on disk.");
            return true;
        }

        result = new BenchmarkModelResolutionResult(
            reference,
            $"manifest:{alias}",
            candidates,
            candidates.Any(candidate => candidate.CandidateKey.Equals("variant:default", StringComparison.OrdinalIgnoreCase))
                ? "variant:default"
                : SelectDefaultCandidateKey(candidates));
        return true;
    }

    private BenchmarkModelResolutionResult? TryDiscoverCachedManifestCandidates(
        string reference,
        string alias,
        BundledModelManifestEntry entry)
    {
        if (string.IsNullOrWhiteSpace(_modelCacheDirectory))
        {
            return null;
        }

        string cachedRootDirectory = ResolveModelCacheRootDirectory(_modelCacheDirectory, entry.ModelId);
        if (!Directory.Exists(cachedRootDirectory))
        {
            return null;
        }

        BenchmarkModelResolutionResult cacheDiscovery = DiscoverFromDirectory(reference, cachedRootDirectory);
        if (cacheDiscovery.Candidates.Count == 0)
        {
            return null;
        }

        return cacheDiscovery with
        {
            ScopeKey = $"cache:{alias}"
        };
    }

    private static string ResolveModelCacheRootDirectory(string modelCacheDirectory, string modelId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelId);

        string root = Path.GetFullPath(modelCacheDirectory);
        foreach (string part in modelId.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (part is "." or "..")
            {
                throw new InvalidOperationException($"Model id '{modelId}' contains an unsafe path segment.");
            }

            root = Path.Combine(root, part);
        }

        return Path.GetFullPath(root);
    }

    private static BenchmarkModelResolutionResult CreateSingleCandidateResult(
        string requestedReference,
        string scopeKey,
        BenchmarkModelCandidate candidate) =>
        new(
            requestedReference,
            scopeKey,
            [candidate],
            candidate.CandidateKey);

    private static BenchmarkModelResolutionResult DiscoverFromDirectory(string requestedReference, string directoryPath)
    {
        string fullDirectoryPath = Path.GetFullPath(directoryPath);
        List<(string RootPath, string RelativePath, string FullPath)> benchmarkableFiles = EnumerateBenchmarkableFiles(fullDirectoryPath);
        if (benchmarkableFiles.Count == 0)
        {
            return new BenchmarkModelResolutionResult(
                requestedReference,
                $"directory:{fullDirectoryPath}",
                [],
                null,
                "Model directory did not contain a resolvable ONNX model entry point.");
        }

        var candidates = benchmarkableFiles
            .Select(file =>
            {
                string displayName = Path.GetFileNameWithoutExtension(file.FullPath);
                string? variantAlias = InferVariantAlias(file.FullPath);
                return new BenchmarkModelCandidate(
                    CandidateKey: $"directory:{file.RelativePath.Replace('\\', '/')}",
                    DisplayName: displayName,
                    ModelPath: file.FullPath,
                    VariantAlias: variantAlias,
                    ResolutionNote: $"Resolved model directory '{requestedReference}' to '{file.FullPath}'.",
                    RootDirectory: fullDirectoryPath);
            })
            .ToArray();

        string? defaultCandidateKey = SelectDefaultCandidateKey(candidates);
        return new BenchmarkModelResolutionResult(
            requestedReference,
            $"directory:{fullDirectoryPath}",
            candidates,
            defaultCandidateKey);
    }

    private static List<(string RootPath, string RelativePath, string FullPath)> EnumerateBenchmarkableFiles(string directoryPath)
    {
        string[] searchRoots =
        [
            directoryPath,
            Path.Combine(directoryPath, "onnx")
        ];

        foreach (string root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!Directory.Exists(root))
            {
                continue;
            }

            string[] files = SelectBenchmarkEntryFiles(root);
            if (files.Length > 0)
            {
                return files
                    .Select(file => (root, Path.GetRelativePath(directoryPath, file), Path.GetFullPath(file)))
                    .ToList();
            }
        }

        return [];
    }

    private static string[] SelectBenchmarkEntryFiles(string rootPath)
    {
        string[] allFiles = Directory.GetFiles(rootPath, "*.onnx", SearchOption.TopDirectoryOnly);
        if (allFiles.Length == 0)
        {
            return [];
        }

        foreach (Func<string, bool> matcher in new Func<string, bool>[]
                 {
                     static fileName => fileName.Equals("silero_vad.onnx", StringComparison.OrdinalIgnoreCase),
                     static fileName => fileName.Equals("encoder.onnx", StringComparison.OrdinalIgnoreCase),
                     static fileName => fileName.StartsWith("encoder_model", StringComparison.OrdinalIgnoreCase),
                     static fileName => fileName.StartsWith("model", StringComparison.OrdinalIgnoreCase),
                     static fileName => fileName.StartsWith("speech_encoder", StringComparison.OrdinalIgnoreCase)
                 })
        {
            string[] matches = allFiles
                .Where(file => matcher(Path.GetFileName(file)))
                .ToArray();

            if (matches.Length > 0)
            {
                return matches;
            }
        }

        return allFiles;
    }

    private static string? SelectDefaultCandidateKey(IReadOnlyList<BenchmarkModelCandidate> candidates)
    {
        foreach (string preferredFileName in PreferredEntryFileNames)
        {
            BenchmarkModelCandidate? preferredCandidate = candidates.FirstOrDefault(
                candidate => Path.GetFileName(candidate.ModelPath).Equals(preferredFileName, StringComparison.OrdinalIgnoreCase));

            if (preferredCandidate is not null)
            {
                return preferredCandidate.CandidateKey;
            }
        }

        return candidates.Count == 1 ? candidates[0].CandidateKey : null;
    }

    private static string? InferVariantAlias(string modelPath)
    {
        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(modelPath);
        if (fileNameWithoutExtension.Equals("model", StringComparison.OrdinalIgnoreCase) ||
            fileNameWithoutExtension.Equals("encoder.onnx", StringComparison.OrdinalIgnoreCase) ||
            fileNameWithoutExtension.Equals("encoder_model", StringComparison.OrdinalIgnoreCase) ||
            fileNameWithoutExtension.Equals("speech_encoder", StringComparison.OrdinalIgnoreCase) ||
            fileNameWithoutExtension.Equals("silero_vad", StringComparison.OrdinalIgnoreCase))
        {
            return "default";
        }

        return fileNameWithoutExtension;
    }

    private static bool VariantMatches(BenchmarkModelCandidate candidate, string explicitVariantAlias)
    {
        string trimmedVariant = explicitVariantAlias.Trim();
        if (string.IsNullOrWhiteSpace(trimmedVariant))
        {
            return false;
        }

        return string.Equals(candidate.VariantAlias, trimmedVariant, StringComparison.OrdinalIgnoreCase)
            || string.Equals(candidate.DisplayName, trimmedVariant, StringComparison.OrdinalIgnoreCase)
            || string.Equals(Path.GetFileNameWithoutExtension(candidate.ModelPath), trimmedVariant, StringComparison.OrdinalIgnoreCase);
    }

    private static string ExpandToAbsolutePath(string path) =>
        Path.IsPathRooted(path)
            ? Path.GetFullPath(path)
            : Path.GetFullPath(Path.Combine(Environment.CurrentDirectory, path));

    private static IEnumerable<string> DiscoverModelStores()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (string seed in new[] { Environment.CurrentDirectory, AppContext.BaseDirectory })
        {
            foreach (string ancestor in EnumerateAncestors(seed))
            {
                string modelStore = Path.Combine(ancestor, "models");
                if (Directory.Exists(modelStore) && seen.Add(modelStore))
                {
                    yield return modelStore;
                }
            }
        }
    }

    private static IEnumerable<string> EnumerateAncestors(string path)
    {
        DirectoryInfo? current = new DirectoryInfo(Path.GetFullPath(path));
        while (current is not null)
        {
            yield return current.FullName;
            current = current.Parent;
        }
    }

    private static IEnumerable<string> FindScopedDirectories(string modelStore, string suppliedPath)
    {
        if (string.IsNullOrWhiteSpace(suppliedPath))
        {
            yield break;
        }

        string normalizedScope = suppliedPath.Replace('/', '\\').Trim('\\');
        string aliasName = Path.GetFileName(normalizedScope);
        if (string.IsNullOrWhiteSpace(aliasName))
        {
            yield break;
        }

        foreach (string directory in Directory.EnumerateDirectories(modelStore, "*", SearchOption.AllDirectories))
        {
            if (aliasName.Equals(Path.GetFileName(directory), StringComparison.OrdinalIgnoreCase))
            {
                yield return directory;
            }
        }
    }
}

public sealed record BenchmarkModelResolutionResult(
    string RequestedReference,
    string ScopeKey,
    IReadOnlyList<BenchmarkModelCandidate> Candidates,
    string? DefaultCandidateKey,
    string? Error = null);

public sealed record BenchmarkModelCandidate(
    string CandidateKey,
    string DisplayName,
    string ModelPath,
    string? VariantAlias,
    string ResolutionNote,
    string? RootDirectory = null);
