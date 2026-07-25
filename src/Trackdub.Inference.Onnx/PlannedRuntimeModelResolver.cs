using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx;

internal static class PlannedRuntimeModelResolver
{
    private const string GenAiConfigFileName = "genai_config.json";

    public static string ResolveModelPath(
        StageRuntimePlan plan,
        BenchmarkModelPathResolver modelPathResolver) =>
        ResolveCandidate(plan, modelPathResolver).ModelPath;

    public static BenchmarkModelCandidate ResolveCandidate(
        StageRuntimePlan plan,
        BenchmarkModelPathResolver modelPathResolver)
    {
        if (!string.IsNullOrWhiteSpace(plan.ModelEntryPath) && File.Exists(plan.ModelEntryPath))
        {
            string modelPath = Path.GetFullPath(plan.ModelEntryPath);
            return new BenchmarkModelCandidate(
                $"planned:{modelPath}",
                plan.ModelAlias ?? Path.GetFileNameWithoutExtension(modelPath),
                modelPath,
                plan.Variant,
                "Resolved the runtime planner's selected model entry path.",
                !string.IsNullOrWhiteSpace(plan.ModelRootPath)
                    ? Path.GetFullPath(plan.ModelRootPath)
                    : InferRootDirectory(modelPath));
        }

        return modelPathResolver.ResolveSingle(plan.ModelAlias!, plan.Variant);
    }

    public static string ResolveModelRootPath(
        StageRuntimePlan plan,
        BenchmarkModelPathResolver modelPathResolver)
    {
        if (!string.IsNullOrWhiteSpace(plan.ModelEntryPath))
        {
            string entryPath = Path.GetFullPath(plan.ModelEntryPath);
            if (File.Exists(entryPath) &&
                string.Equals(Path.GetFileName(entryPath), GenAiConfigFileName, StringComparison.OrdinalIgnoreCase))
            {
                return Path.GetDirectoryName(entryPath)
                    ?? throw new InvalidOperationException(
                        $"Cannot resolve GenAI model root directory for alias '{plan.ModelAlias}'.");
            }
        }

        BenchmarkModelCandidate candidate = ResolveCandidate(plan, modelPathResolver);
        return !string.IsNullOrWhiteSpace(plan.ModelRootPath)
            ? Path.GetFullPath(plan.ModelRootPath)
            : candidate.RootDirectory
            ?? Path.GetDirectoryName(candidate.ModelPath)
            ?? throw new InvalidOperationException($"Cannot resolve model root directory for alias '{plan.ModelAlias}'.");
    }

    private static string? InferRootDirectory(string modelPath)
    {
        string? modelDirectory = Path.GetDirectoryName(modelPath);
        if (modelDirectory is null)
        {
            return null;
        }

        return string.Equals(Path.GetFileName(modelDirectory), "onnx", StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(modelDirectory) ?? modelDirectory
            : modelDirectory;
    }
}
