namespace Trackdub.Benchmarks;

public sealed record AudioPrepBenchmarkManifest(
    IReadOnlyList<AudioPrepBenchmarkFixture> Fixtures);

public sealed record AudioPrepBenchmarkFixture(
    string Id,
    string? FullMixPath,
    string? VocalStemPath,
    string? ReferenceTranscript,
    double? ReferenceSpeechCoverageSeconds,
    IReadOnlyList<AudioPrepBenchmarkProfileInput> Results);

public sealed record AudioPrepBenchmarkProfileInput(
    string ProfileId,
    string? Transcript,
    int? SpeechRegionCount,
    double? SpeechCoverageSeconds,
    int? SpeakerCount,
    int? TurnCount,
    double? ProcessingSeconds,
    IReadOnlyList<string>? GuardrailFailures);

public sealed record AudioPrepBenchmarkReport(
    string ManifestPath,
    string ReportPath,
    IReadOnlyList<AudioPrepBenchmarkFixtureReport> Fixtures,
    AudioPrepBenchmarkAggregate Aggregate,
    DateTimeOffset GeneratedAtUtc);

public sealed record AudioPrepBenchmarkFixtureReport(
    string FixtureId,
    IReadOnlyList<AudioPrepBenchmarkProfileReport> Profiles,
    AudioPrepBenchmarkComparison? AutoComparison);

public sealed record AudioPrepBenchmarkProfileReport(
    string ProfileId,
    double? WordErrorRate,
    double? CharacterErrorRate,
    double BlankTranscriptRate,
    double RepetitionRate,
    int? SpeechRegionCount,
    double? SpeechCoverageSeconds,
    int? SpeakerCount,
    int? TurnCount,
    double? ProcessingSeconds,
    IReadOnlyList<string> GuardrailFailures);

public sealed record AudioPrepBenchmarkComparison(
    double? WordErrorRateDelta,
    double? CharacterErrorRateDelta,
    double? SpeechCoverageDeltaSeconds,
    int? SpeechRegionCountDelta,
    int? SpeakerCountDrift,
    double? TurnFragmentationIncreaseRatio,
    bool DiarizationFallbackAccepted,
    bool Accepted);

public sealed record AudioPrepBenchmarkAggregate(
    int FixtureCount,
    int AutoComparisonCount,
    int AcceptedAutoCount,
    double? AverageWordErrorRateDelta,
    double? AverageCharacterErrorRateDelta,
    double? AverageSpeechCoverageDeltaSeconds,
    double? AverageTurnFragmentationIncreaseRatio);
