using Trackdub.Domain;

namespace Trackdub.Application.ModelOptimization;

public sealed class OliveRecipeResolver
{
    private static readonly HashSet<string> PilotEngineFamilies = new(StringComparer.OrdinalIgnoreCase)
    {
        "whisper-genai",
        "whisper-onnx",
        "phi-genai"
    };

    public OliveRecipeResolution Resolve(
        string modelId,
        string engineFamily,
        IReadOnlyList<ModelOptimizationRecipeBinding> recipeBindings,
        OliveExecutionProvider executionProvider,
        string precision,
        string? recipesRoot,
        string? explicitRecipeConfigPath = null,
        ModelOptimizationFallbackPolicy profileFallbackPolicy = ModelOptimizationFallbackPolicy.None)
    {
        if (!string.IsNullOrWhiteSpace(explicitRecipeConfigPath))
        {
            string overridePath = Path.GetFullPath(explicitRecipeConfigPath);
            if (!File.Exists(overridePath))
            {
                return OliveRecipeResolution.Fail($"Olive recipe override not found: '{overridePath}'.");
            }

            return OliveRecipeResolution.Recipe(overridePath, source: "modellab-override");
        }

        if (string.IsNullOrWhiteSpace(recipesRoot) || !Directory.Exists(recipesRoot))
        {
            return OliveRecipeResolution.AutoOpt("olive-recipes root is not configured.");
        }

        if (!PilotEngineFamilies.Contains(engineFamily))
        {
            return OliveRecipeResolution.AutoOpt($"Engine family '{engineFamily}' is outside the recipe pilot.");
        }

        string? providerKey = MapProviderKey(executionProvider);
        string normalizedPrecision = NormalizePrecision(precision);

        ModelOptimizationRecipeBinding? binding = recipeBindings
            .Where(candidate => candidate.Provider is null ||
                                candidate.Provider.Equals(providerKey, StringComparison.OrdinalIgnoreCase))
            .Where(candidate => candidate.Precision is null ||
                                candidate.Precision.Equals(normalizedPrecision, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Provider is not null)
            .ThenByDescending(candidate => candidate.Precision is not null)
            .FirstOrDefault();

        ModelOptimizationFallbackPolicy effectivePolicy = binding?.FallbackPolicy ?? profileFallbackPolicy;

        if (binding is null || string.IsNullOrWhiteSpace(binding.ConfigRelativePath))
        {
            return FallbackOrFail(effectivePolicy, "No manifest recipe binding matched provider and precision.");
        }

        string relativeConfigPath = binding.ConfigRelativePath
            .Replace('\\', Path.DirectorySeparatorChar)
            .Replace('/', Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(relativeConfigPath))
        {
            return FallbackOrFail(effectivePolicy, $"Recipe binding config path is rooted ('{relativeConfigPath}'); expected a relative path.");
        }

        string configPath = Path.GetFullPath(Path.Join(recipesRoot, relativeConfigPath));
        if (!IsStrictSubpathOrEqual(configPath, Path.GetFullPath(recipesRoot)))
        {
            return FallbackOrFail(effectivePolicy, $"Recipe config path escapes recipes root: '{relativeConfigPath}'.");
        }
        if (!File.Exists(configPath))
        {
            return FallbackOrFail(effectivePolicy, $"Recipe config missing at '{configPath}'.");
        }

        return OliveRecipeResolution.Recipe(configPath, source: "manifest-binding", selectedBinding: binding);
    }

    private static OliveRecipeResolution FallbackOrFail(ModelOptimizationFallbackPolicy policy, string reason) =>
        policy switch
        {
            ModelOptimizationFallbackPolicy.AutoOptAllowed => OliveRecipeResolution.AutoOpt(reason, source: policy.ToString()),
            _ => OliveRecipeResolution.Fail(reason)
        };

    public static string MapProviderKey(OliveExecutionProvider provider) =>
        provider switch
        {
            OliveExecutionProvider.Dml => "dml",
            OliveExecutionProvider.Cuda => "cuda",
            OliveExecutionProvider.TensorRt => "tensorrt",
            OliveExecutionProvider.TensorRtRtx => "trt-rtx",
            OliveExecutionProvider.Migraphx => "migraphx",
            OliveExecutionProvider.Rocm => "rocm",
            OliveExecutionProvider.VitisAi => "vitisai",
            OliveExecutionProvider.Qnn => "qnn",
            OliveExecutionProvider.OpenVino => "openvino",
            _ => "cpu"
        };

    public static string NormalizePrecision(string precision) =>
        string.IsNullOrWhiteSpace(precision)
            ? "fp32"
            : precision.Trim().ToLowerInvariant();

    public static string ToRecipeFolderId(string modelId) =>
        modelId.Replace("/", "-", StringComparison.Ordinal);

    private static bool IsStrictSubpathOrEqual(string path, string ancestor)
    {
        string ancestorFull = Path.GetFullPath(ancestor);
        string pathFull = Path.GetFullPath(path);
        string prefix = AppendDirectorySeparator(ancestorFull);
        return pathFull.Equals(Path.TrimEndingDirectorySeparator(ancestorFull), StringComparison.OrdinalIgnoreCase)
            || pathFull.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendDirectorySeparator(string path)
    {
        string full = Path.GetFullPath(path);
        char sep = Path.DirectorySeparatorChar;
        return full.EndsWith(sep) ? full : full + sep;
    }
}

public sealed record OliveRecipeResolution(
    bool UseRecipe,
    string? RecipeConfigPath,
    string? Source,
    string? FallbackReason,
    ModelOptimizationRecipeBinding? SelectedBinding = null,
    bool IsHardFailure = false)
{
    public static OliveRecipeResolution Recipe(
        string configPath,
        string source,
        ModelOptimizationRecipeBinding? selectedBinding = null) =>
        new(true, configPath, source, null, selectedBinding);

    public static OliveRecipeResolution AutoOpt(string? reason = null, string? source = null) =>
        new(false, null, source, reason);

    public static OliveRecipeResolution Fail(string reason) =>
        new(false, null, null, reason, IsHardFailure: true);
}
