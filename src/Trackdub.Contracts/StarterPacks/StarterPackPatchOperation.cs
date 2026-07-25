namespace Trackdub.Contracts.StarterPacks;

public enum StarterPackPatchKind
{
    SetStageExecutionProvider,
    SwapStageModelAlias,
    SetOptionalModelEnabled,
    FlagNotRunnable
}

public sealed record StarterPackPatchOperation(
    StarterPackPatchKind Kind,
    string? Stage,
    string? Value,
    string Reason);

public sealed record StarterPackPatchResult(
    StarterPackDefinition Patched,
    IReadOnlyList<StarterPackPatchOperation> Applied,
    IReadOnlyList<StarterPackPatchOperation> Rejected,
    bool AnyApplied);
