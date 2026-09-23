using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Trackdub.Infrastructure.ModelOptimization;

/// <summary>
/// Turns a checked-in Olive recipe (resources/olive-recipes) into a runnable one: resolves the
/// ${MODEL_ROOT} / ${TRT_RTX_EP_PATH} placeholders the recipes share with tools/olive/Validate-*.ps1.
/// </summary>
internal static partial class OliveRecipePreparation
{
    public const string ModelRootPlaceholder = "MODEL_ROOT";
    public const string TrtRtxEpPathPlaceholder = "TRT_RTX_EP_PATH";
    public const string TrtRtxEpDirectoryEnvironmentVariable = "TRACKDUB_TRT_RTX_EP_DIR";
    public const string PruneScriptResourceName = "Trackdub.Infrastructure.ModelOptimization.prune_attention_outputs.py";

    private const string TrtRtxPluginFileNameWindows = "onnxruntime_providers_nv_tensorrt_rtx.dll";
    private const string TrtRtxPluginFileNameLinux = "libonnxruntime_providers_nv_tensorrt_rtx.so";

    [GeneratedRegex(@"\$\{([A-Z0-9_]+)\}")]
    private static partial Regex PlaceholderPattern();

    public static IReadOnlySet<string> FindPlaceholders(string recipeText) =>
        PlaceholderPattern().Matches(recipeText).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);

    /// <summary>
    /// Substitutes placeholders inside JSON string literals. Values are written with forward
    /// slashes and JSON-escaped so Windows paths stay valid JSON.
    /// </summary>
    public static string ResolvePlaceholders(string recipeText, IReadOnlyDictionary<string, string> values) =>
        PlaceholderPattern().Replace(recipeText, match =>
        {
            string name = match.Groups[1].Value;
            if (!values.TryGetValue(name, out string? value))
            {
                throw new InvalidOperationException($"Olive recipe placeholder '${{{name}}}' has no value.");
            }

            string encoded = JsonSerializer.Serialize(value.Replace('\\', '/'));
            return encoded[1..^1];
        });

    /// <summary>
    /// Mirrors TensorRtRtxPluginLocator's order minus the Studio setting: TRACKDUB_TRT_RTX_EP_DIR, then
    /// the installed bundle under &lt;UserDataRoot&gt;/Providers/trt-rtx/&lt;version&gt;/&lt;cuda&gt;/&lt;rid&gt;
    /// (newest version first).
    /// </summary>
    public static string? FindTrtRtxProviderLibrary(string userDataRoot, Func<string, string?> getEnvironmentVariable)
    {
        string fileName = OperatingSystem.IsWindows() ? TrtRtxPluginFileNameWindows : TrtRtxPluginFileNameLinux;

        string? environmentDirectory = getEnvironmentVariable(TrtRtxEpDirectoryEnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(environmentDirectory))
        {
            string candidate = Path.Combine(environmentDirectory, fileName);
            return File.Exists(candidate) ? Path.GetFullPath(candidate) : null;
        }

        string providersRoot = Path.Combine(userDataRoot, "Providers", "trt-rtx");
        if (!Directory.Exists(providersRoot))
        {
            return null;
        }

        string runtimeIdentifier = OperatingSystem.IsWindows() ? "win-x64" : "linux-x64";
        return Directory.EnumerateDirectories(providersRoot)
            .OrderByDescending(directory => Version.TryParse(Path.GetFileName(directory), out Version? version) ? version : new Version(0, 0))
            .SelectMany(Directory.EnumerateDirectories)
            .Select(cudaDirectory => Path.Combine(cudaDirectory, runtimeIdentifier, fileName))
            .FirstOrDefault(File.Exists);
    }

    public static string? GetInputModelPath(JsonNode recipe) =>
        recipe["input_model"]?["model_path"] is JsonValue value && value.TryGetValue(out string? path) ? path : null;

    public static void SetInputModelPath(JsonNode recipe, string modelPath) =>
        recipe["input_model"]!["model_path"] = modelPath.Replace('\\', '/');

    public static string ExtractPruneScript(string directory)
    {
        using Stream stream = typeof(OliveRecipePreparation).Assembly.GetManifestResourceStream(PruneScriptResourceName)
            ?? throw new InvalidOperationException($"Embedded resource '{PruneScriptResourceName}' is missing.");
        string scriptPath = Path.Combine(directory, "prune_attention_outputs.py");
        using FileStream file = File.Create(scriptPath);
        stream.CopyTo(file);
        return scriptPath;
    }
}
