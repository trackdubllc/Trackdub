using System.Text.Json;

using Trackdub.Contracts.Pipeline;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Shared full-pipeline execution for <c>dub</c> and <c>run pipeline</c>.
/// </summary>
internal static class RunPipelineHandler
{
    public static async Task<int> ExecuteAsync(
        TrackdubSessionFactory factory,
        RunPipelineRequest request,
        IProgress<PipelineProgressEvent>? progress,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        var engine = new TrackdubDubbingEngine(factory);

        var dubbingOptions = new DubbingSessionOptions
        {
            SourceMediaPath = request.SourceMediaPath,
            ProjectOutputDirectory = request.ProjectOutputDirectory,
            SourceLanguageCode = request.SourceLanguageCode,
            TargetLanguageCode = request.TargetLanguageCode,
            ModelPreferences = request.ModelPreferences,
            ExportFormat = request.ExportFormat,
            StageFilter = request.StageFilter,
            ForceRerun = request.ForceRerun,
            EnableAsrTextRefinement = request.EnableAsrTextRefinement,
        };

        DubbingRunResult result;
        try
        {
            result = await engine.ExecuteAsync(dubbingOptions, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CliErrorReporter.ReportError(ErrorCode.Cancelled, "Pipeline execution was cancelled.");
            return Program.ExitPipelineFailure;
        }

        string projectOutputDirectory = request.ProjectOutputDirectory
            ?? Path.Combine(
                Path.GetDirectoryName(request.SourceMediaPath) ?? ".",
                Path.GetFileNameWithoutExtension(request.SourceMediaPath) + ".trackdub");

        var manifestWriter = new RunManifestWriter();
        await manifestWriter.WriteAsync(result, projectOutputDirectory, cancellationToken).ConfigureAwait(false);
        string manifestPath = Path.Combine(projectOutputDirectory, "run-manifest.json");

        if (result.OverallStatus is DubbingRunStatus.Succeeded or DubbingRunStatus.PartialSuccess)
        {
            string? exportedFilePath = result.StageOutcomes
                .Where(o => string.Equals(o.StageName, "Export", StringComparison.OrdinalIgnoreCase))
                .Where(o => o.Status == StageStatus.Succeeded)
                .SelectMany(o => o.ArtifactPaths)
                .FirstOrDefault();

            var payload = new RunPipelineOutput
            {
                ExportedFilePath = exportedFilePath,
                ManifestPath = manifestPath,
                Status = result.OverallStatus.ToString(),
            };

            string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
            await output.WriteLineAsync(json).ConfigureAwait(false);
            return Program.ExitSuccess;
        }

        foreach (StageOutcome failedStage in result.StageOutcomes.Where(o => o.Status == StageStatus.Failed))
        {
            CliErrorReporter.ReportStageFailure(
                ErrorCode.StageFailed,
                failedStage.StageName,
                failedStage.ReasonCode ?? "Stage execution failed",
                failedStage.ArtifactPaths);
        }

        if (!result.StageOutcomes.Any(o => o.Status == StageStatus.Failed))
        {
            foreach (StageOutcome skippedStage in result.StageOutcomes.Where(o => o.Status == StageStatus.Skipped))
            {
                CliErrorReporter.ReportStageFailure(
                    ErrorCode.StagePrerequisiteMissing,
                    skippedStage.StageName,
                    skippedStage.ReasonCode ?? "Stage skipped",
                    artifactPaths: null);
            }
        }

        if (result.PreFlightFailures is { Count: > 0 })
        {
            foreach (string failure in result.PreFlightFailures)
            {
                CliErrorReporter.ReportError(ErrorCode.PreFlightFailed, failure);
            }
        }

        var failurePayload = new RunPipelineOutput
        {
            ManifestPath = manifestPath,
            Status = result.OverallStatus.ToString(),
        };

        string failureJson = JsonSerializer.Serialize(failurePayload, CliJsonOptions.Default);
        await output.WriteLineAsync(failureJson).ConfigureAwait(false);
        return Program.ExitPipelineFailure;
    }

    internal sealed record RunPipelineRequest
    {
        public required string SourceMediaPath { get; init; }
        public string? ProjectOutputDirectory { get; init; }
        public string? SourceLanguageCode { get; init; }
        public required string TargetLanguageCode { get; init; }
        public IReadOnlyDictionary<string, string>? ModelPreferences { get; init; }
        public string? ExportFormat { get; init; }
        public IReadOnlyList<string>? StageFilter { get; init; }
        public bool ForceRerun { get; init; }
        public bool EnableAsrTextRefinement { get; init; }
    }

    private sealed class RunPipelineOutput
    {
        public string? ExportedFilePath { get; init; }
        public string? ManifestPath { get; init; }
        public string? Status { get; init; }
    }
}
