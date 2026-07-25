namespace Trackdub.Contracts.StarterPacks;

public sealed record StarterPackApplyBlock(
    string? TierPreference,
    IReadOnlyDictionary<string, string>? StageAliases,
    IReadOnlyDictionary<string, string>? Overrides,
    IReadOnlyDictionary<string, string>? CloudStages);
