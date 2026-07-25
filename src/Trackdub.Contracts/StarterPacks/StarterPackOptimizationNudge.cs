namespace Trackdub.Contracts.StarterPacks;

public sealed record StarterPackOptimizationNudge(
    string ModelId,
    string Alias,
    string? TargetVariant = null);
