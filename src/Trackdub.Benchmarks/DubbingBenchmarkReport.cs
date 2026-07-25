using Trackdub.Contracts.Dubbing;

namespace Trackdub.Benchmarks;

/// <summary>
/// Immutable benchmark report produced by <see cref="DubbingBenchmarkRunner"/>.
/// Carries per-stage wall-clock timings from the real pipeline (not estimates).
/// </summary>
public sealed record DubbingBenchmarkReport(
    string InputPath,
    string TargetLanguage,
    TimeSpan TotalDuration,
    TimeSpan AsrDuration,
    TimeSpan TranslationDuration,
    TimeSpan TtsDuration,
    TimeSpan MixingDuration,
    int SegmentCount,
    string HardwareInfo,
    DateTimeOffset StartedAtUtc,
    DateTimeOffset CompletedAtUtc,
    IReadOnlyList<StageOutcome>? StageOutcomes = null,
    string? Error = null)
{
    public bool Success => Error is null;

    /// <summary>
    /// Default save location for the JSON report.
    /// </summary>
    public string ReportPath { get; init; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "TrackdubBenchmarks",
            $"dubbing-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
}
