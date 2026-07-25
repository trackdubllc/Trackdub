using Trackdub.Domain;

namespace Trackdub.Contracts.ModelOptimization;

public sealed record ModelOptimizedVariantRegistration(
    string ModelId,
    string BaseModelRootPath,
    string VariantAlias,
    string VariantRootPath,
    string EntryRelativePath,
    IReadOnlyList<string> ComponentRelativePaths,
    string OptimizerId,
    ExecutionProviderKind ExecutionProvider,
    string Precision,
    DateTimeOffset CreatedAtUtc,
    ModelOptimizedVariantProvenance? Provenance = null);
