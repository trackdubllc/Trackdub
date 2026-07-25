namespace Trackdub.Contracts.ModelOptimization;

using Trackdub.Domain;

public sealed record ModelOptimizationRequest(
    string ModelId,
    string ModelRootPath,
    string OutputVariantPath,
    OliveExecutionProvider ExecutionProvider,
    string Precision,
    IReadOnlyList<string> ComponentRelativePaths,
    string? VariantAlias = null,
    string? EntryRelativePath = null,
    string? OliveMode = null,
    string? OliveRecipeConfigPath = null,
    bool UseSharedComponentCache = false,
    IReadOnlyList<ModelOptimizationOperation>? Operations = null,
    ModelOptimizationExpectedOutput ExpectedOutput = ModelOptimizationExpectedOutput.OnnxComponents,
    ModelOptimizationFallbackPolicy FallbackPolicy = ModelOptimizationFallbackPolicy.None,
    string? QuantizationMethod = null,
    string? RecipeConfigHash = null,
    string? OutputManifestRelativePath = null)
{
    public IReadOnlyList<ModelOptimizationOperation> Operations { get; init; } = Operations ?? [];
}
