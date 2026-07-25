using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Executes a single pipeline stage against an existing on-disk project.
/// </summary>
internal static class RunStageHandler
{
    private static readonly JsonSerializerOptions s_outputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> ExecuteAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        string stageName,
        string? modelAlias,
        IProgress<PipelineProgressEvent>? progress,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string resolvedProjectPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(resolvedProjectPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.ProjectNotFound,
                $"Project directory not found: {resolvedProjectPath}",
                "--project");
            return Program.ExitPipelineFailure;
        }

        if (!TrackdubProjectPaths.ContainsDatabase(resolvedProjectPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.ProjectNotFound,
                $"No valid .trackdub project found in: {resolvedProjectPath}",
                "--project");
            return Program.ExitPipelineFailure;
        }

        TrackdubProjectContext? projectContext = await TrackdubProjectContextResolver
            .TryOpenAsync(factory, resolvedProjectPath, cancellationToken)
            .ConfigureAwait(false);

        if (projectContext is null)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.ProjectNotFound,
                $"Failed to open project at: {resolvedProjectPath}",
                "--project");
            return Program.ExitPipelineFailure;
        }

        string sourceMediaPath = ResolveSourceMediaPath(
            resolvedProjectPath,
            stageName,
            projectContext.SourceMediaPath);

        if (TrackdubPipelineStages.RequiresSourceMedia(stageName)
            && string.IsNullOrWhiteSpace(projectContext.SourceMediaPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.MediaNotFound,
                "Project has no stored source media path. Re-ingest or relocate source media before running this stage.",
                "--project");
            return Program.ExitArgumentError;
        }

        if (TrackdubPipelineStages.RequiresTargetLanguage(stageName)
            && string.IsNullOrWhiteSpace(projectContext.TargetLanguageCode))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "Project has no target language. Set a target language before running this stage.",
                "--target-language");
            return Program.ExitArgumentError;
        }

        Dictionary<string, string>? modelPreferences = null;
        if (modelAlias is not null)
        {
            modelPreferences = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [stageName] = modelAlias,
            };
        }

        var engine = new TrackdubDubbingEngine(factory);
        var dubbingOptions = new DubbingSessionOptions
        {
            SourceMediaPath = sourceMediaPath,
            ProjectOutputDirectory = resolvedProjectPath,
            TargetLanguageCode = projectContext.TargetLanguageCode ?? string.Empty,
            ModelPreferences = modelPreferences,
            StageFilter = [stageName],
            ForceRerun = true,
        };

        DubbingRunResult result;
        try
        {
            result = await engine.ExecuteAsync(dubbingOptions, progress, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            CliErrorReporter.ReportError(ErrorCode.Cancelled, "Stage execution was cancelled.");
            return Program.ExitPipelineFailure;
        }

        StageOutcome? stageOutcome = result.StageOutcomes
            .FirstOrDefault(o => string.Equals(o.StageName, stageName, StringComparison.OrdinalIgnoreCase));

        if (result.OverallStatus == DubbingRunStatus.PreFlightFailed)
        {
            if (result.PreFlightFailures is { Count: > 0 })
            {
                foreach (string failure in result.PreFlightFailures)
                {
                    CliErrorReporter.ReportError(ErrorCode.StagePrerequisiteMissing, failure);
                }
            }

            return Program.ExitPipelineFailure;
        }

        if (stageOutcome is null || stageOutcome.Status == StageStatus.Failed)
        {
            CliErrorReporter.ReportStageFailure(
                ErrorCode.StageFailed,
                stageName,
                stageOutcome?.ReasonCode ?? "Stage execution failed",
                stageOutcome?.ArtifactPaths);
            return Program.ExitPipelineFailure;
        }

        if (stageOutcome.Status == StageStatus.Skipped)
        {
            string reason = stageOutcome.ReasonCode ?? "PREREQUISITE_FAILED";
            CliErrorReporter.ReportStageFailure(
                ErrorCode.StagePrerequisiteMissing,
                stageName,
                $"Stage skipped: {reason}",
                artifactPaths: null);
            return Program.ExitPipelineFailure;
        }

        var payload = new RunStageOutput
        {
            Stage = stageName,
            Status = stageOutcome.Status.ToString(),
            ArtifactPaths = stageOutcome.ArtifactPaths.Count > 0 ? stageOutcome.ArtifactPaths : null,
            ElapsedSeconds = (stageOutcome.EndTime - stageOutcome.StartTime).TotalSeconds,
        };

        string json = JsonSerializer.Serialize(payload, s_outputJsonOptions);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    private static string ResolveSourceMediaPath(
        string projectRootPath,
        string stageName,
        string? storedSourceMediaPath)
    {
        if (!string.IsNullOrWhiteSpace(storedSourceMediaPath))
        {
            return storedSourceMediaPath;
        }

        return Path.Combine(projectRootPath, "source-media");
    }

    private sealed class RunStageOutput
    {
        public string? Stage { get; init; }
        public string? Status { get; init; }
        public IReadOnlyList<string>? ArtifactPaths { get; init; }
        public double ElapsedSeconds { get; init; }
    }
}
