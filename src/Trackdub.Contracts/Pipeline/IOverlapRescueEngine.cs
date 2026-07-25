namespace Trackdub.Contracts.Pipeline;

public interface IOverlapRescueEngine
{
    Task<OverlapRescueResult> RescueAsync(
        OverlapRescueRequest request,
        IProgress<OverlapRescueProgress>? progress,
        CancellationToken cancellationToken);
}

public sealed record OverlapRescueRequest(
    string RegionAudioPath,
    string SourceCandidate0OutputPath,
    string SourceCandidate1OutputPath,
    double RegionStartSeconds,
    double RegionEndSeconds,
    string? PreferredModelAlias = null,
    string? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record OverlapRescueResult(
    double DurationSeconds,
    int SampleRate,
    int ChannelCount,
    bool PermutationWarning,
    IReadOnlyDictionary<string, string>? Metadata = null);

public sealed record OverlapRescueProgress(
    int CompletedRegions,
    int TotalRegions,
    int RegionIndex,
    double RegionStartSeconds,
    double RegionEndSeconds,
    bool IsPersistingArtifacts = false)
{
    public double PercentComplete => TotalRegions <= 0
        ? 0d
        : Math.Clamp(CompletedRegions / (double)TotalRegions, 0d, 1d);
}
