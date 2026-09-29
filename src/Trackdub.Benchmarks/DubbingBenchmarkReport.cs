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

    /// <summary>Identity of the engine run that produced this report (empty when the run never started).</summary>
    public Guid RunId { get; init; }

    /// <summary>
    /// Milliseconds from run start until the first structured
    /// <see cref="Trackdub.Contracts.Pipeline.PipelineOutputKind.TranscriptSegmentAvailable"/>
    /// event. Null when no such event was observed — never derived from stage completion.
    /// </summary>
    public double? FirstUsableTranscriptMilliseconds { get; init; }

    /// <summary>
    /// Milliseconds from run start until the first transcript segment was persisted
    /// (repository save and artifact write completed).
    /// </summary>
    public double? FirstPersistedTranscriptMilliseconds { get; init; }

    /// <summary>
    /// Milliseconds from run start until the first playable dubbed-audio take was persisted.
    /// </summary>
    public double? FirstPlayableAudioMilliseconds { get; init; }

    /// <summary>Runtime version evidence captured at report time.</summary>
    public IReadOnlyDictionary<string, string> RuntimeVersions { get; init; } = new Dictionary<string, string>();

    /// <summary>Phase timings observed through the ambient phase capture (phase:* keys).</summary>
    public IReadOnlyDictionary<string, double?> PhaseTimingsMilliseconds { get; init; } = new Dictionary<string, double?>();

    /// <summary>Measured counters and maxima recorded by the ambient phase capture.</summary>
    public IReadOnlyDictionary<string, long?> Counters { get; init; } = new Dictionary<string, long?>();

    /// <summary>
    /// Default save location for the JSON report.
    /// </summary>
    public string ReportPath { get; init; } =
        Path.Join(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "TrackdubBenchmarks",
            $"dubbing-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json");
}
