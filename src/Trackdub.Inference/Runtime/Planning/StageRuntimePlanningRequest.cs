using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

public sealed record StageRuntimePlanningRequest(
    RuntimeStage Stage,
    string? PreferredModelAlias = null,
    string? PreferredEngineFamily = null,
    string? PreferredModelTier = null,
    string? SourceLanguage = null,
    string? TargetLanguage = null,
    bool RequirePreferredModelAlias = false,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null,
    DeviceExclusionSet? DeviceExclusions = null)
{
    public string? NormalizedPreferredModelAlias =>
        string.IsNullOrWhiteSpace(PreferredModelAlias)
            ? null
            : PreferredModelAlias.Trim();

    public string? NormalizedPreferredModelVariantAlias =>
        string.IsNullOrWhiteSpace(PreferredModelVariantAlias)
            ? null
            : PreferredModelVariantAlias.Trim();
}
