using Trackdub.Domain;

namespace Trackdub.Contracts;

public interface IHardwareProfilerService
{
    Task<HardwareProfilerViewState> GetViewStateAsync(CancellationToken cancellationToken = default);

    Task<HardwareProfilerRunResult> RunBenchmarkSuiteAsync(CancellationToken cancellationToken = default);

    string ResolveEffectiveModelTierPreference(StudioSettings settings);
}

public sealed record HardwareProfilerViewState(
    HardwareProfilerSnapshot? Snapshot,
    bool IsStale,
    HardwareQualityPreset EffectivePreset,
    HardwarePresetRecommendation? EffectiveRecommendation,
    string? OverridePresetKey,
    bool HasOverride,
    string? EvidenceIdForPlanner)
{
    public bool HasCompletedBenchmark => Snapshot is not null;

    public bool BenchmarkAvailableForPlanner =>
        Snapshot is not null && !IsStale;
}

public sealed record HardwareProfilerRunResult(
    bool Succeeded,
    HardwareProfilerSnapshot? Snapshot,
    string? ErrorMessage)
{
    public static HardwareProfilerRunResult Success(HardwareProfilerSnapshot snapshot) =>
        new(true, snapshot, null);

    public static HardwareProfilerRunResult Failure(string errorMessage) =>
        new(false, null, errorMessage);
}
