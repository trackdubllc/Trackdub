namespace Trackdub.Benchmarks.Scenarios;

/// <summary>
/// Configuration options for executing an Execution Provider Matrix comparison benchmark.
/// </summary>
public sealed record ExecutionProviderMatrixOptions
{
    /// <summary>The benchmark scenario name (e.g. "full-pipeline", "asr", "separation").</summary>
    public required string Scenario { get; init; }

    /// <summary>Path to the input media fixture file.</summary>
    public required string FixturePath { get; init; }

    /// <summary>Output directory where evidence and matrix reports are persisted.</summary>
    public required string OutputDirectory { get; init; }

    /// <summary>Execution providers to benchmark and compare. Defaults to ["cpu", "directml", "tensorrt"].</summary>
    public IReadOnlyList<string> Providers { get; init; } = ["cpu", "directml", "tensorrt"];

    /// <summary>The baseline provider to use as the reference for relative speedup and deltas. Defaults to "cpu".</summary>
    public string BaselineProvider { get; init; } = "cpu";

    /// <summary>Number of iterations to execute for each provider.</summary>
    public int RunCount { get; init; } = 1;

    /// <summary>Optional expected SHA-256 hash of the fixture file for integrity verification.</summary>
    public string? ExpectedFixtureSha256 { get; init; }

    /// <summary>Target language code for dubbing stages (defaults to "es").</summary>
    public string TargetLanguage { get; init; } = "es";

    /// <summary>Source language code (null for automatic detection).</summary>
    public string? SourceLanguage { get; init; }

    /// <summary>Execution mode: "fresh-process", "warm-host", or "artifact-resume". Defaults to "fresh-process".</summary>
    public string Mode { get; init; } = "fresh-process";

    /// <summary>Whether to reuse the ONNX EP compilation engine cache across runs.</summary>
    public bool ReuseEngineCache { get; init; }

    /// <summary>Optional custom model directory path.</summary>
    public string? ModelDirectory { get; init; }

    /// <summary>Optional custom path to the ffmpeg executable.</summary>
    public string? FfmpegPath { get; init; }

    /// <summary>Optional custom path to the ffprobe executable.</summary>
    public string? FfprobePath { get; init; }

    /// <summary>Model alias overrides per stage.</summary>
    public IReadOnlyDictionary<string, string> ModelOverrides { get; init; } =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Whether to execute in deterministic mock/dry-run mode without live models or GPU.</summary>
    public bool Mock { get; init; }

    /// <summary>Report output format: Console, Json, or Both.</summary>
    public ReportFormat ReportFormat { get; init; } = ReportFormat.Both;

    /// <summary>Alias for ReportFormat.</summary>
    public ReportFormat Format
    {
        get => ReportFormat;
        init => ReportFormat = value;
    }
}
