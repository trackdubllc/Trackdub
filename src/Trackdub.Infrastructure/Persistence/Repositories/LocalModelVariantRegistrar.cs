using Trackdub.Contracts.ModelOptimization;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Persistence.Repositories;

public sealed class LocalModelVariantRegistrar(LocalModelCacheRecordStore recordStore) : IModelVariantRegistrar
{
    private readonly LocalModelCacheRecordStore recordStore = recordStore ?? throw new ArgumentNullException(nameof(recordStore));

    public async Task RegisterAsync(
        ModelOptimizedVariantRegistration registration,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(registration);

        await recordStore.MutateAsync(
            loaded =>
            {
                LocalModelCacheRecord[] records = loaded.ToArray();
                int recordIndex = Array.FindIndex(records, record =>
                    record.ModelId.Equals(registration.ModelId, StringComparison.OrdinalIgnoreCase) &&
                    PathsEqual(record.RootPath, registration.BaseModelRootPath));

                if (recordIndex < 0)
                {
                    throw new InvalidOperationException(
                        $"Cannot register optimized variant '{registration.VariantAlias}' because the base model cache record was not found.");
                }

                LocalModelCacheRecord baseRecord = records[recordIndex];
                LocalModelVariantRecord variant = BuildVariant(baseRecord, registration);
                List<LocalModelVariantRecord> variants = baseRecord.Variants
                    .Where(existing => !existing.Alias.Equals(variant.Alias, StringComparison.OrdinalIgnoreCase))
                    .Append(variant)
                    .OrderBy(existing => existing.Alias, StringComparer.OrdinalIgnoreCase)
                    .ToList();

                records[recordIndex] = baseRecord with { Variants = variants };
                return records;
            },
            cancellationToken).ConfigureAwait(false);
    }

    private static LocalModelVariantRecord BuildVariant(
        LocalModelCacheRecord baseRecord,
        ModelOptimizedVariantRegistration registration)
    {
        if (string.IsNullOrWhiteSpace(registration.VariantAlias))
        {
            throw new InvalidOperationException("Optimized variant alias is required.");
        }

        if (string.IsNullOrWhiteSpace(registration.OptimizerId))
        {
            throw new InvalidOperationException("Optimized variant optimizer id is required.");
        }

        if (registration.ComponentRelativePaths.Count == 0)
        {
            throw new InvalidOperationException("Optimized variant must declare at least one component.");
        }

        string baseRoot = Path.GetFullPath(registration.BaseModelRootPath);
        string variantRoot = Path.GetFullPath(registration.VariantRootPath);
        if (!IsStrictSubpathOrEqual(variantRoot, baseRoot))
        {
            throw new InvalidOperationException($"Optimized variant path is invalid: {registration.VariantRootPath}.");
        }

        if (!Directory.Exists(variantRoot))
        {
            throw new InvalidOperationException($"Optimized variant path is missing: {registration.VariantRootPath}.");
        }

        string entryPath = ResolveOnnxRelativePath(variantRoot, registration.EntryRelativePath, "entry");
        if (!File.Exists(entryPath))
        {
            throw new InvalidOperationException($"Optimized variant entry missing: {registration.EntryRelativePath}.");
        }

        foreach (string componentRelativePath in registration.ComponentRelativePaths)
        {
            string componentPath = ResolveOnnxRelativePath(variantRoot, componentRelativePath, "component");
            if (!File.Exists(componentPath))
            {
                throw new InvalidOperationException($"Optimized variant component missing: {componentRelativePath}.");
            }
        }

        return new LocalModelVariantRecord(
            registration.VariantAlias,
            variantRoot,
            NormalizeRelativePath(registration.EntryRelativePath),
            registration.ComponentRelativePaths.Select(NormalizeRelativePath).ToArray(),
            registration.OptimizerId,
            registration.ExecutionProvider,
            registration.Precision,
            registration.CreatedAtUtc,
            baseRecord.Revision,
            string.IsNullOrWhiteSpace(baseRecord.Sha256) ? null : baseRecord.Sha256,
            IntegrityFailed: false,
            Provenance: registration.Provenance);
    }

    private static string ResolveOnnxRelativePath(string rootPath, string relativePath, string label)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || IsRootedLikePath(relativePath))
        {
            throw new InvalidOperationException($"Optimized variant {label} path is invalid: {relativePath}.");
        }

        string normalized = NormalizeRelativePath(relativePath);
        if (normalized.Split('/').Any(segment => segment is "." or ".." || string.IsNullOrWhiteSpace(segment)))
        {
            throw new InvalidOperationException($"Optimized variant {label} path is invalid: {relativePath}.");
        }

        // Allow both .onnx files and genai_config.json
        bool isOnnx = normalized.EndsWith(".onnx", StringComparison.OrdinalIgnoreCase);
        bool isGenAiConfig = Path.GetFileName(normalized).Equals("genai_config.json", StringComparison.OrdinalIgnoreCase);

        if (!isOnnx && !isGenAiConfig)
        {
            throw new InvalidOperationException(
                $"Optimized variant {label} path must reference an ONNX file or genai_config.json: {relativePath}.");
        }

        string root = Path.GetFullPath(rootPath);
        string candidate = Path.GetFullPath(Path.Combine(root, normalized.Replace('/', Path.DirectorySeparatorChar)));
        if (!IsStrictSubpathOrEqual(candidate, root))
        {
            throw new InvalidOperationException($"Optimized variant {label} path is invalid: {relativePath}.");
        }

        return candidate;
    }

    private static string NormalizeRelativePath(string relativePath) =>
        relativePath.Replace('\\', '/');

    private static bool PathsEqual(string left, string right) =>
        string.Equals(Path.GetFullPath(left), Path.GetFullPath(right), StringComparison.OrdinalIgnoreCase);

    private static bool IsRootedLikePath(string path)
    {
        string normalized = path.Replace('\\', '/');
        return Path.IsPathRooted(path) ||
            normalized.StartsWith("/", StringComparison.Ordinal) ||
            (normalized.Length >= 2 && char.IsLetter(normalized[0]) && normalized[1] == ':');
    }

    private static bool IsStrictSubpathOrEqual(string candidatePath, string rootPath)
    {
        string root = AppendDirectorySeparator(Path.GetFullPath(rootPath));
        string candidate = Path.GetFullPath(candidatePath);
        return candidate.Equals(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar), StringComparison.OrdinalIgnoreCase) ||
            candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendDirectorySeparator(string path)
    {
        char last = path[^1];
        return last == Path.DirectorySeparatorChar || last == Path.AltDirectorySeparatorChar
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
