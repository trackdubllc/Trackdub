namespace Trackdub.Benchmarks;

/// <summary>One isolated, explicit pipeline measurement.</summary>
public sealed record ControlledDubbingBenchmarkOptions
{
    public required string FixturePath { get; init; }
    public string? ExpectedFixtureSha256 { get; init; }
    public required string OutputDirectory { get; init; }
    public string TargetLanguage { get; init; } = "es";
    public string? SourceLanguage { get; init; }
    public string? Stage { get; init; }
    public string? Model { get; init; }
    public string? Provider { get; init; }
    public string Mode { get; init; } = "fresh-process";
    public bool ReuseEngineCache { get; init; }
    public string? ModelDirectory { get; init; }
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public int RunCount { get; init; } = 1;
}
