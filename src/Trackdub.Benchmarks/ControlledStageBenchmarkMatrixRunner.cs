using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks;

public sealed record ControlledStageBenchmarkMatrixResult(
    string Stage,
    BenchmarkEvidenceReport Evidence,
    LatencyStatistics? Statistics = null);

public sealed record ControlledStageBenchmarkMatrixReport
{
    public required string FixturePath { get; init; }
    public required IReadOnlyList<ControlledStageBenchmarkMatrixResult> Results { get; init; }
    public required BenchmarkEvidenceStatus Status { get; init; }
    public required DateTimeOffset StartedAtUtc { get; init; }
    public required DateTimeOffset CompletedAtUtc { get; init; }
    public string ReportPath { get; init; } = string.Empty;

    public bool Success => Status == BenchmarkEvidenceStatus.Completed;
}

/// <summary>Runs the existing controlled benchmark once for each selected pipeline stage.</summary>
public sealed class ControlledStageBenchmarkMatrixRunner : IDisposable
{
    private readonly ControlledDubbingBenchmarkRunner runner;

    public ControlledStageBenchmarkMatrixRunner(ControlledDubbingBenchmarkRunner? runner = null)
    {
        this.runner = runner ?? new();
    }

    public async Task<ControlledStageBenchmarkMatrixReport> RunAsync(
        ControlledStageBenchmarkMatrixOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> selectedStages = ResolveStages(options.Stages);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var results = new List<ControlledStageBenchmarkMatrixResult>(selectedStages.Count);

        foreach (string stage in selectedStages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            options.ModelOverrides.TryGetValue(stage, out string? model);
            BenchmarkEvidenceReport evidence = await runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = options.FixturePath,
                    ExpectedFixtureSha256 = options.ExpectedFixtureSha256,
                    OutputDirectory = Path.Combine(options.OutputDirectory, stage),
                    Stage = stage,
                    Model = model,
                    Provider = options.Provider,
                    Mode = options.Mode,
                    ReuseEngineCache = options.ReuseEngineCache,
                    TargetLanguage = options.TargetLanguage,
                    SourceLanguage = options.SourceLanguage,
                    ModelDirectory = options.ModelDirectory,
                    FfmpegPath = options.FfmpegPath,
                    FfprobePath = options.FfprobePath,
                    RunCount = options.RunCount,
                    Mock = options.Mock,
                    DryRun = options.DryRun,
                }, cancellationToken).ConfigureAwait(false);

            LatencyStatistics? stats = null;
            if (evidence.TimingsMilliseconds.TryGetValue($"stage:{stage}:p50", out double? p50) && p50.HasValue)
            {
                stats = new LatencyStatistics(
                    MinMilliseconds: evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:min") ?? p50.Value,
                    MaxMilliseconds: evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:max") ?? p50.Value,
                    MeanMilliseconds: evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:mean") ?? p50.Value,
                    P50Milliseconds: p50.Value,
                    P90Milliseconds: evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:p90") ?? p50.Value,
                    P99Milliseconds: evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:p99") ?? p50.Value,
                    ThroughputUnitsPerSecond: evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:throughput") ?? 0d,
                    SampleCount: (int)(evidence.TimingsMilliseconds.GetValueOrDefault($"stage:{stage}:sampleCount") ?? 1d));
            }

            results.Add(new ControlledStageBenchmarkMatrixResult(stage, evidence, stats));
        }

        BenchmarkEvidenceStatus status = results.All(
            result => result.Evidence.Status == BenchmarkEvidenceStatus.Completed)
            ? BenchmarkEvidenceStatus.Completed
            : results.Any(result => result.Evidence.Status == BenchmarkEvidenceStatus.Canceled)
                ? BenchmarkEvidenceStatus.Canceled
                : results.Any(result => result.Evidence.Status == BenchmarkEvidenceStatus.Failed)
                    ? BenchmarkEvidenceStatus.Failed
                    : BenchmarkEvidenceStatus.PartiallyCompleted;

        return new ControlledStageBenchmarkMatrixReport
        {
            FixturePath = options.FixturePath,
            Results = results,
            Status = status,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ReportPath = Path.Combine(
                options.OutputDirectory,
                $"stage-matrix-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json"),
        };
    }

    public void Dispose() => runner.Dispose();

    private static IReadOnlyList<string> ResolveStages(IReadOnlyList<string> requested)
    {
        if (requested.Count == 0)
        {
            return DubbingPipelineStages.ExtendedStageOrder;
        }

        var requestedSet = new HashSet<string>(requested, StringComparer.OrdinalIgnoreCase);
        string[] unknown = requested
            .Where(stage => !DubbingPipelineStages.ExtendedStageOrder.Contains(
                stage, StringComparer.OrdinalIgnoreCase))
            .ToArray();
        if (unknown.Length > 0)
        {
            throw new ArgumentException(
                $"Unknown pipeline stage(s): {string.Join(", ", unknown)}.",
                nameof(requested));
        }

        return DubbingPipelineStages.ExtendedStageOrder
            .Where(requestedSet.Contains)
            .ToArray();
    }
}
