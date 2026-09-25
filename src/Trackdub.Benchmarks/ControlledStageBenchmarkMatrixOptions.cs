namespace Trackdub.Benchmarks;

/// <summary>Options for running the controlled benchmark once per focused pipeline stage.</summary>
public sealed record ControlledStageBenchmarkMatrixOptions
{
    public required string FixturePath { get; init; }
    public required string OutputDirectory { get; init; }
    public IReadOnlyList<string> Stages { get; init; } = [];
    public string? ExpectedFixtureSha256 { get; init; }
    public string TargetLanguage { get; init; } = "es";
    public string? SourceLanguage { get; init; }
    public string Mode { get; init; } = "fresh-process";
    public bool ReuseEngineCache { get; init; }
    public string? ModelDirectory { get; init; }
    public string? Provider { get; init; }
    public string? FfmpegPath { get; init; }
    public string? FfprobePath { get; init; }
    public IReadOnlyDictionary<string, string> ModelOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    public int RunCount { get; init; } = 1;
    public bool Mock { get; init; }
    public bool DryRun { get; init; }
}
