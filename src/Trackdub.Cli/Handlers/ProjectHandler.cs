using System.Text.Json;

using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Projects;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

/// <summary>
/// Creates and inspects Trackdub project spines without auto-starting transcription.
/// </summary>
internal static class ProjectHandler
{
    public static async Task<int> CreateAsync(
        TrackdubSessionFactory factory,
        string mediaPath,
        string? projectName,
        string? outputDirectory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        (int exitCode, ProjectCreateResult? result) = await TryCreateAsync(
            factory,
            mediaPath,
            projectName,
            outputDirectory,
            cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return exitCode;
        }

        var payload = new ProjectCreateOutput
        {
            ProjectPath = result.ProjectPath,
            ProjectId = result.ProjectId,
            SourceMediaPath = result.SourceMediaPath,
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return exitCode;
    }

    public static async Task<int> OpenAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        string resolvedProjectPath = Path.GetFullPath(projectPath);
        if (!ValidateProjectRoot(resolvedProjectPath, out int validationExitCode))
        {
            return validationExitCode;
        }

        await using TrackdubSession session = factory.CreateSession(resolvedProjectPath);
        TranscriptProjectState state = await session.Workspace.Project
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        ProjectSummaryOutput payload = BuildSummary(resolvedProjectPath, state);
        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    public static async Task<int> InfoAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ProjectDetailSnapshot? detail = await TryLoadDetailAsync(factory, projectPath, cancellationToken)
            .ConfigureAwait(false);

        if (detail is null)
        {
            return Program.ExitArgumentError;
        }

        string json = JsonSerializer.Serialize(detail, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    internal static async Task<ProjectDetailSnapshot?> TryLoadDetailAsync(
        TrackdubSessionFactory factory,
        string projectPath,
        CancellationToken cancellationToken)
    {
        string resolvedProjectPath = Path.GetFullPath(projectPath);
        if (!ValidateProjectRoot(resolvedProjectPath, out _))
        {
            return null;
        }

        await using TrackdubSession session = factory.CreateSession(resolvedProjectPath);
        TranscriptProjectState state = await session.Workspace.Project
            .OpenAsync(cancellationToken)
            .ConfigureAwait(false);

        return BuildDetail(resolvedProjectPath, state);
    }

    internal static async Task<(int ExitCode, ProjectCreateResult? Result)> TryCreateAsync(
        TrackdubSessionFactory factory,
        string mediaPath,
        string? projectName,
        string? outputDirectory,
        CancellationToken cancellationToken)
    {
        string resolvedMediaPath = Path.GetFullPath(mediaPath);
        if (!File.Exists(resolvedMediaPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.MediaNotFound,
                $"Media file not found: {resolvedMediaPath}",
                "--media");
            return (Program.ExitArgumentError, null);
        }

        string resolvedOutputDirectory = outputDirectory is not null
            ? Path.GetFullPath(outputDirectory)
            : Path.Combine(
                Path.GetDirectoryName(resolvedMediaPath) ?? ".",
                Path.GetFileNameWithoutExtension(resolvedMediaPath) + ".trackdub");

        string resolvedProjectName = string.IsNullOrWhiteSpace(projectName)
            ? Path.GetFileNameWithoutExtension(resolvedMediaPath)
            : projectName.Trim();

        Directory.CreateDirectory(resolvedOutputDirectory);

        await using TrackdubSession session = factory.CreateSession(resolvedOutputDirectory);
        TranscriptProjectState state = await session.Workspace
            .CreateMediaSpineAsync(
                new CreateTranscriptProjectRequest(resolvedProjectName, resolvedMediaPath),
                cancellationToken)
            .ConfigureAwait(false);

        var result = new ProjectCreateResult
        {
            ProjectPath = resolvedOutputDirectory,
            ProjectId = state.ProjectState.Project.Id.ToString(),
            ProjectName = resolvedProjectName,
            SourceMediaPath = state.ProjectState.SourceReference?.OriginalPath
                ?? state.ProjectState.MediaAsset?.SourceFilePath
                ?? resolvedMediaPath,
        };

        return (Program.ExitSuccess, result);
    }

    private static bool ValidateProjectRoot(string projectRootPath, out int exitCode)
    {
        exitCode = Program.ExitSuccess;

        if (!Directory.Exists(projectRootPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.ProjectNotFound,
                $"Project directory not found: {projectRootPath}",
                "--project");
            exitCode = Program.ExitArgumentError;
            return false;
        }

        if (!TrackdubProjectPaths.ContainsDatabase(projectRootPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.ProjectNotFound,
                $"No valid .trackdub project found in: {projectRootPath}",
                "--project");
            exitCode = Program.ExitArgumentError;
            return false;
        }

        return true;
    }

    private static ProjectSummaryOutput BuildSummary(string projectPath, TranscriptProjectState state) =>
        new()
        {
            ProjectPath = projectPath,
            ProjectId = state.ProjectState.Project.Id.ToString(),
            ProjectName = state.ProjectState.Project.Name,
            SourceMediaPath = state.ProjectState.SourceReference?.OriginalPath
                ?? state.ProjectState.MediaAsset?.SourceFilePath,
            TargetLanguage = state.SelectedTranslationTargetLanguage,
            StageRunCount = state.StageRuns.Count,
        };

    private static ProjectDetailSnapshot BuildDetail(string projectPath, TranscriptProjectState state)
    {
        string? sourceMediaPath = state.ProjectState.SourceReference?.OriginalPath
            ?? state.ProjectState.MediaAsset?.SourceFilePath;

        var artifacts = new List<ProjectArtifactSnapshot>();
        if (!string.IsNullOrWhiteSpace(state.AsrAudioRelativePath))
        {
            artifacts.Add(new ProjectArtifactSnapshot
            {
                Kind = "normalized-audio",
                RelativePath = state.AsrAudioRelativePath,
                Exists = File.Exists(Path.Combine(projectPath, state.AsrAudioRelativePath)),
            });
        }

        if (state.CurrentTranscriptRevision is not null)
        {
            artifacts.Add(new ProjectArtifactSnapshot
            {
                Kind = "transcript",
                RelativePath = "transcript",
                Exists = state.TranscriptSegments.Count > 0,
            });
        }

        if (state.CurrentTranslationRevision is not null)
        {
            artifacts.Add(new ProjectArtifactSnapshot
            {
                Kind = "translation",
                RelativePath = "translation",
                Exists = state.TranslatedSegments.Count > 0,
            });
        }

        return new ProjectDetailSnapshot
        {
            ProjectPath = projectPath,
            ProjectId = state.ProjectState.Project.Id.ToString(),
            ProjectName = state.ProjectState.Project.Name,
            SourceMediaPath = sourceMediaPath,
            TargetLanguage = state.SelectedTranslationTargetLanguage,
            Artifacts = artifacts,
            StageRuns = state.StageRuns
                .Select(run => new ProjectStageRunSnapshot
                {
                    Stage = run.StageName,
                    Status = run.Status.ToString(),
                    StartedAtUtc = run.StartedAtUtc,
                    CompletedAtUtc = run.CompletedAtUtc,
                    ReasonCode = run.FailureReason,
                })
                .OrderByDescending(run => run.CompletedAtUtc ?? run.StartedAtUtc)
                .ToList(),
        };
    }

    internal sealed record ProjectCreateResult
    {
        public string? ProjectPath { get; init; }
        public string? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public string? SourceMediaPath { get; init; }
    }

    internal sealed record ProjectDetailSnapshot
    {
        public string? ProjectPath { get; init; }
        public string? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public string? SourceMediaPath { get; init; }
        public string? TargetLanguage { get; init; }
        public IReadOnlyList<ProjectArtifactSnapshot>? Artifacts { get; init; }
        public IReadOnlyList<ProjectStageRunSnapshot>? StageRuns { get; init; }
    }

    internal sealed record ProjectArtifactSnapshot
    {
        public string? Kind { get; init; }
        public string? RelativePath { get; init; }
        public bool Exists { get; init; }
    }

    internal sealed record ProjectStageRunSnapshot
    {
        public string? Stage { get; init; }
        public string? Status { get; init; }
        public DateTimeOffset StartedAtUtc { get; init; }
        public DateTimeOffset? CompletedAtUtc { get; init; }
        public string? ReasonCode { get; init; }
    }

    private sealed class ProjectCreateOutput
    {
        public string? ProjectPath { get; init; }
        public string? ProjectId { get; init; }
        public string? SourceMediaPath { get; init; }
    }

    private sealed class ProjectSummaryOutput
    {
        public string? ProjectPath { get; init; }
        public string? ProjectId { get; init; }
        public string? ProjectName { get; init; }
        public string? SourceMediaPath { get; init; }
        public string? TargetLanguage { get; init; }
        public int StageRunCount { get; init; }
    }
}
