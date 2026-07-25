namespace Trackdub.Contracts.StarterPacks;

public interface IStarterPackCompatibilityService
{
    Task<StarterPackCompatibilityReport> EvaluateAsync(
        string packId,
        string profileId,
        StarterPackHardwareProfile? hardwareProfile = null,
        CancellationToken cancellationToken = default);
}

public sealed record StageCompatibilityEntry(
    string Stage,
    string Alias,
    string RequestedVariant,
    string RequestedExecutionProvider,
    string ResolvedVariant,
    string ResolvedExecutionProvider,
    bool FallbackApplied,
    string? FallbackReason,
    bool Runnable)
{
    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record StarterPackCompatibilityReport(
    string PackId,
    string ProfileId,
    string HardwareProfileKey,
    IReadOnlyList<StageCompatibilityEntry> Stages,
    bool AllStagesRunnable,
    bool AnyFallbackApplied)
{
    public string CompatibilityStatus =>
        !AllStagesRunnable
            ? "not_runnable"
            : AnyFallbackApplied
                ? "fallbacks_required"
                : "fully_compatible";
}

public sealed record StageFallbackRecord(
    string Stage,
    string Alias,
    string RequestedVariant,
    string RequestedExecutionProvider,
    string ResolvedVariant,
    string ResolvedExecutionProvider,
    string? FallbackReason);
