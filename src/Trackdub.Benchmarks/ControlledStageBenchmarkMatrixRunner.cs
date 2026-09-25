using Trackdub.Application.Dubbing;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Benchmarks;

public sealed record ControlledStageBenchmarkMatrixResult(
    string Stage,
    BenchmarkEvidenceReport Evidence);

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
    private readonly ControlledDubbingBenchmarkRunner runner = new();

    public async Task<ControlledStageBenchmarkMatrixReport> RunAsync(
        ControlledStageBenchmarkMatrixOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        IReadOnlyList<string> selectedStages = ResolveStages(options.Stages);
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var results = new List<ControlledStageBenchmarkMatrixResult>(selectedStages.Count);
        string outputBasePath = Path.GetFullPath(options.OutputDirectory);
        if (!outputBasePath.EndsWith(Path.DirectorySeparatorChar) &&
            !outputBasePath.EndsWith(Path.AltDirectorySeparatorChar))
        {
            outputBasePath += Path.DirectorySeparatorChar;
        }

        foreach (string stage in selectedStages)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Path.IsPathRooted(stage))
            {
                throw new ArgumentException(
                    "Stage names must be relative path segments.",
                    nameof(options));
            }

            string stageDirectoryName = Path.GetFileName(stage);
            if (string.IsNullOrWhiteSpace(stageDirectoryName) || Path.IsPathRooted(stageDirectoryName))
            {
                throw new ArgumentException(
                    "Stage names must resolve to a relative directory name.",
                    nameof(options));
            }

            string combinedOutputDirectory = Path.GetFullPath(Path.Join(outputBasePath, stageDirectoryName));
            if (!combinedOutputDirectory.StartsWith(outputBasePath, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException(
                    "Stage names must resolve within the output directory.",
                    nameof(options));
            }

            options.ModelOverrides.TryGetValue(stage, out string? model);
            BenchmarkEvidenceReport evidence = await runner.RunAsync(
                new ControlledDubbingBenchmarkOptions
                {
                    FixturePath = options.FixturePath,
                    ExpectedFixtureSha256 = options.ExpectedFixtureSha256,
                    OutputDirectory = combinedOutputDirectory,
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
                }, cancellationToken).ConfigureAwait(false);

            results.Add(new ControlledStageBenchmarkMatrixResult(stage, evidence));
        }

        BenchmarkEvidenceStatus status = results.All(
            result => result.Evidence.Status == BenchmarkEvidenceStatus.Completed)
            ? BenchmarkEvidenceStatus.Completed
            : results.Any(result => result.Evidence.Status == BenchmarkEvidenceStatus.Canceled)
                ? BenchmarkEvidenceStatus.Canceled
                : results.Any(result => result.Evidence.Status == BenchmarkEvidenceStatus.Failed)
                    ? BenchmarkEvidenceStatus.Failed
                    : BenchmarkEvidenceStatus.PartiallyCompleted;

        string reportFileName = $"stage-matrix-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss}.json";
        if (Path.IsPathRooted(reportFileName))
        {
            throw new InvalidOperationException("Report file name must be a relative path.");
        }

        return new ControlledStageBenchmarkMatrixReport
        {
            FixturePath = options.FixturePath,
            Results = results,
            Status = status,
            StartedAtUtc = startedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            ReportPath = Path.Combine(options.OutputDirectory, reportFileName),
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
