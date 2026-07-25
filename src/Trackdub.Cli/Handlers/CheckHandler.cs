using System.Text.Json;
using System.Text.Json.Serialization;

using Trackdub.Contracts.Pipeline;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Runs the <c>check</c> command against real pipeline readiness evaluation.
/// </summary>
internal static class CheckHandler
{
    private static readonly JsonSerializerOptions s_outputJsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static async Task<int> ExecuteAsync(
        TrackdubSessionFactory factory,
        string? projectPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        if (projectPath is not null)
        {
            string resolvedProjectPath = Path.GetFullPath(projectPath);
            if (!Directory.Exists(resolvedProjectPath))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.ProjectNotFound,
                    $"Project directory not found: {resolvedProjectPath}",
                    "--project");
                return Program.ExitArgumentError;
            }

            if (!TrackdubProjectPaths.ContainsDatabase(resolvedProjectPath))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.ProjectNotFound,
                    $"No valid .trackdub project found in: {resolvedProjectPath}",
                    "--project");
                return Program.ExitArgumentError;
            }

            projectPath = resolvedProjectPath;
        }

        var checker = new TrackdubPipelineReadinessChecker(factory);
        PipelineReadinessReport report = await checker
            .EvaluateDefaultPipelineAsync(projectPath, cancellationToken)
            .ConfigureAwait(false);

        var payload = new CheckOutput
        {
            Ready = report.IsRunReady,
            Stages = report.Stages
                .Select(static stage => new StageReadinessRow
                {
                    Stage = stage.StageName,
                    Ready = !stage.Status.IsBlocking(),
                    ReadinessState = stage.Status,
                    Detail = stage.Detail,
                    ModelId = stage.ModelId,
                    ModelAlias = stage.ModelAlias,
                    ResolveAction = stage.ResolveAction,
                })
                .ToList(),
        };

        string json = JsonSerializer.Serialize(payload, s_outputJsonOptions);
        await output.WriteLineAsync(json).ConfigureAwait(false);

        return report.IsRunReady ? Program.ExitSuccess : Program.ExitPipelineFailure;
    }

    private sealed class CheckOutput
    {
        public bool Ready { get; init; }
        public List<StageReadinessRow>? Stages { get; init; }
    }

    private sealed class StageReadinessRow
    {
        public string? Stage { get; init; }
        public bool Ready { get; init; }
        public ReadinessState ReadinessState { get; init; }
        public string? Detail { get; init; }
        public string? ModelId { get; init; }
        public string? ModelAlias { get; init; }
        public string? ResolveAction { get; init; }
    }
}
