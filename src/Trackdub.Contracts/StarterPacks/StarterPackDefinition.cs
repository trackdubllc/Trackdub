namespace Trackdub.Contracts.StarterPacks;

public sealed record StarterPackRuntimeDefaults(
    string Variant,
    string ExecutionProvider);

public sealed record StarterPackModelDefinition(
    string ModelId,
    string Stage,
    bool Required,
    string Alias,
    IReadOnlyDictionary<string, StarterPackRuntimeDefaults> RuntimeDefaults,
    string Source = "local",
    string VariantPreference = "auto");

public sealed record StarterPackProfileDefinition(
    string Id,
    string DisplayName,
    string? AsrModelId = null);

public sealed record StarterPackTranslationDefinition(
    string Strategy,
    string ModelId,
    string Alias);

public sealed record StarterPackDefinition(
    int SchemaVersion,
    string Id,
    string DisplayName,
    string TierPreference,
    string Description,
    IReadOnlyList<StarterPackProfileDefinition> Profiles,
    IReadOnlyList<StarterPackModelDefinition> Models,
    StarterPackTranslationDefinition? Translation = null,
    IReadOnlyList<string>? OptionalModels = null,
    bool OliveAutoRun = false,
    StarterPackKind PackKind = StarterPackKind.Local,
    StarterPackCloudDefaults? CloudDefaults = null,
    StarterPackOrigin PackOrigin = StarterPackOrigin.Bundled,
    StarterPackApplyBlock? Apply = null);

public sealed record StarterPackSummary(
    string Id,
    string DisplayName,
    string TierPreference,
    IReadOnlyList<string> ProfileIds,
    int RequiredCount,
    int InstalledCount,
    bool CanApply,
    bool HasCommercialVerificationGap,
    bool RequiresVoiceCloningConsent,
    bool Recommended,
    bool Applied,
    string? BlockedReason,
    string StatusLabel,
    string? CompatibilityStatus = null,
    bool IsRunnable = true,
    IReadOnlyList<string>? CompatibilityWarnings = null,
    StarterPackKind PackKind = StarterPackKind.Local,
    bool CloudCredentialsReady = true,
    bool IsUserPack = false,
    StarterPackGpuSetupHint? GpuSetup = null);

public sealed record StarterPackDownloadResult(
    string PackId,
    string ProfileId,
    bool Success,
    IReadOnlyList<ModelDownloadOutcome> Outcomes,
    string? FailureReason = null);

public sealed record ModelDownloadOutcome(
    string ModelId,
    bool Success,
    string? FailureReason);

public sealed record StarterPackApplyResult(
    string PackId,
    string ProfileId,
    string HardwareProfile,
    bool Success,
    string? FailureReason = null,
    IReadOnlyList<StageFallbackRecord>? Fallbacks = null);

public enum StarterPackHardwareProfile
{
    CpuSafe,
    BalancedGpu,
    TurboGpu
}
