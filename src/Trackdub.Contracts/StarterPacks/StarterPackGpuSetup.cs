namespace Trackdub.Contracts.StarterPacks;

/// <summary>
/// Vendor-specific GPU runtime bundle recommended for starter-pack compatibility.
/// </summary>
public enum StarterPackGpuRuntimeKind
{
    None = 0,
    NvidiaTensorRtRtx,
    AmdMigraphx,
    IntelOpenVino,
    QualcommQnn,
    AmdVitisAi,
    WindowsMlCatalogBundle,
}

/// <summary>
/// Actionable GPU runtime setup state for a local starter pack card.
/// </summary>
public sealed record StarterPackGpuSetupHint(
    StarterPackGpuRuntimeKind RuntimeKind,
    bool CanInstall,
    bool RequiresGpuVariantOptimization,
    int GpuFallbackStageCount);
