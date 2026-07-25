using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Pipeline status and run orchestration shared by CLI and TUI.
/// </summary>
internal static class PipelineHandler
{
    internal static readonly (string StageName, string DisplayName)[] UiStages =
    [
        (StageNames.Separation, "Separation"),
        (StageNames.Vad, "VAD"),
        (StageNames.Asr, "Transcribe"),
        (StageNames.Diarization, "Identify"),
        (StageNames.Translation, "Translate"),
        (StageNames.Tts, "Dub"),
        (StageNames.Export, "Export"),
    ];

    internal sealed record PipelineStageRow(
        string StageName,
        string DisplayName,
        StageRunStatus? LastRunStatus,
        DateTimeOffset? LastRunAtUtc,
        string? FailureReason,
        ReadinessState? ReadinessState,
        bool ReadinessReady,
        string? ReadinessDetail);

    internal sealed record PipelineSnapshot(
        string ProjectPath,
        string ProjectName,
        string? SourceMediaPath,
        string? TargetLanguage,
        IReadOnlyList<PipelineStageRow> Stages,
        bool IsRunReady);

    internal static async Task<PipelineSnapshot?> TryLoadSnapshotAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        CancellationToken cancellationToken)
    {
        string resolvedProjectPath = Path.GetFullPath(projectPath);
        if (!Directory.Exists(resolvedProjectPath)
            || !TrackdubProjectPaths.ContainsDatabase(resolvedProjectPath))
        {
            return null;
        }

        await using TrackdubSession session = factory.CreateSession(resolvedProjectPath);
        TranscriptProjectState state = await session.Workspace.Project
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        var checker = new TrackdubPipelineReadinessChecker(factory);
        PipelineReadinessReport report = await checker
            .EvaluateDefaultPipelineAsync(resolvedProjectPath, cancellationToken)
            .ConfigureAwait(false);

        var readinessByStage = report.Stages.ToDictionary(
            stage => stage.StageName,
            stage => stage,
            StringComparer.OrdinalIgnoreCase);

        var rows = new List<PipelineStageRow>(UiStages.Length);
        foreach ((string stageName, string displayName) in UiStages)
        {
            StageRunRecord? latestRun = GetLatestStageRun(state.StageRuns, stageName);
            readinessByStage.TryGetValue(stageName, out StageReadiness? readiness);

            rows.Add(new PipelineStageRow(
                stageName,
                displayName,
                latestRun?.Status,
                latestRun?.CompletedAtUtc ?? latestRun?.StartedAtUtc,
                latestRun?.FailureReason,
                readiness?.Status,
                readiness is null || !readiness.Status.IsBlocking(),
                readiness?.Detail));
        }

        string? sourceMediaPath = state.ProjectState.SourceReference?.OriginalPath
            ?? state.ProjectState.MediaAsset?.SourceFilePath;

        return new PipelineSnapshot(
            resolvedProjectPath,
            state.ProjectState.Project.Name,
            sourceMediaPath,
            state.SelectedTranslationTargetLanguage,
            rows,
            report.IsRunReady);
    }

    internal static StageRunRecord? GetLatestStageRun(
        IReadOnlyList<StageRunRecord> runs,
        string stageName) =>
        runs
            .Where(run => string.Equals(run.StageName, stageName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(run => run.CompletedAtUtc ?? run.StartedAtUtc)
            .FirstOrDefault();

    internal static Task<int> RunStageAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        string stageName,
        CancellationToken cancellationToken) =>
        CliProgressRunner.ExecuteAsync(
            "text",
            async (progress, ct) =>
            {
                await using var output = new StringWriter();
                return await RunStageHandler.ExecuteAsync(
                    factory,
                    projectPath,
                    stageName,
                    modelAlias: null,
                    progress,
                    output,
                    ct).ConfigureAwait(false);
            },
            cancellationToken);

    internal static async Task<int> RunFullPipelineAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        CancellationToken cancellationToken)
    {
        string resolvedProjectPath = Path.GetFullPath(projectPath);
        TrackdubProjectContext? projectContext = await TrackdubProjectContextResolver
            .TryOpenAsync(factory, resolvedProjectPath, cancellationToken)
            .ConfigureAwait(false);

        if (projectContext is null)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.ProjectNotFound,
                $"Failed to open project at: {resolvedProjectPath}",
                "--project");
            return Program.ExitArgumentError;
        }

        if (string.IsNullOrWhiteSpace(projectContext.SourceMediaPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.MediaNotFound,
                "Project has no stored source media path. Re-ingest before running the pipeline.",
                "--project");
            return Program.ExitArgumentError;
        }

        if (string.IsNullOrWhiteSpace(projectContext.TargetLanguageCode))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "Project has no target language. Set a target language before running the pipeline.",
                "--target-language");
            return Program.ExitArgumentError;
        }

        return await CliProgressRunner.ExecuteAsync(
            "text",
            async (progress, ct) =>
            {
                await using var output = new StringWriter();
                return await RunPipelineHandler.ExecuteAsync(
                    factory,
                    new RunPipelineHandler.RunPipelineRequest
                    {
                        SourceMediaPath = projectContext.SourceMediaPath,
                        ProjectOutputDirectory = resolvedProjectPath,
                        TargetLanguageCode = projectContext.TargetLanguageCode,
                    },
                    progress,
                    output,
                    ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
    }
}
