using Trackdub.Application.Projects;
using Trackdub.Contracts.Projects;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Infrastructure.FileSystem;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Media.Extraction;
using Trackdub.Media.Probe;
using Trackdub.Media.Waveforms;

namespace Trackdub.Tools;

public static class MediaIngestCommand
{
    public static Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        CancellationToken cancellationToken) =>
        RunAsync(args, output, error, new DefaultMediaIngestCommandRunner(), cancellationToken);

    public static async Task<int> RunAsync(
        IReadOnlyList<string> args,
        TextWriter output,
        TextWriter error,
        IMediaIngestCommandRunner runner,
        CancellationToken cancellationToken)
    {
        if (!MediaIngestCommandOptions.TryParse(args, error, out MediaIngestCommandOptions options))
        {
            WriteUsage(error);
            return 1;
        }

        if (options.ShowHelp)
        {
            WriteUsage(output);
            return 0;
        }

        try
        {
            if (options.OpenExistingProject)
            {
                OpenProjectResult openResult = await runner.OpenAsync(options, cancellationToken).ConfigureAwait(false);
                WriteOpenSummary(output, options.ProjectRootPath, openResult);
                return openResult.SourceStatus is SourceMediaStatus.Missing ? 2 : 0;
            }

            CreateProjectFromMediaResult createResult = await runner.CreateAsync(options, cancellationToken).ConfigureAwait(false);
            WriteCreateSummary(output, options.ProjectRootPath, createResult);
            return 0;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex)
        {
            error.WriteLine(ex.ToString());
            return 1;
        }
    }

    public static void WriteUsage(TextWriter writer)
    {
        writer.WriteLine("Trackdub.Tools ingest");
        writer.WriteLine();
        writer.WriteLine("Usage:");
        writer.WriteLine("  Trackdub.Tools ingest --project <path> --name <project-name> --media <path> [--ffmpeg <path>] [--ffprobe <path>]");
        writer.WriteLine("  Trackdub.Tools ingest --project <path> --open [--ffmpeg <path>] [--ffprobe <path>]");
        writer.WriteLine();
        writer.WriteLine("Options:");
        writer.WriteLine("  --project <path>    Required project root, typically ending in .trackdub.");
        writer.WriteLine("  --name <name>       Required for create mode. Project display name.");
        writer.WriteLine("  --media <path>      Required for create mode. Source media file to ingest.");
        writer.WriteLine("  --open              Open an existing project and report source/artifact status.");
        writer.WriteLine("  --ffmpeg <path>     Optional explicit ffmpeg executable path.");
        writer.WriteLine("  --ffprobe <path>    Optional explicit ffprobe executable path.");
        writer.WriteLine("  --help              Show this help.");
    }

    private static void WriteCreateSummary(
        TextWriter writer,
        string projectRootPath,
        CreateProjectFromMediaResult result)
    {
        int registeredArtifactCount = new ProjectArtifact[] { result.AudioArtifact, result.WaveformArtifact }.Length;
        writer.WriteLine($"Created project: {result.Project.Name}");
        writer.WriteLine($"Project root: {projectRootPath}");
        writer.WriteLine($"Database: {Path.Combine(projectRootPath, ProjectArtifactPaths.DatabaseFileName)}");
        writer.WriteLine($"Manifest: {Path.Combine(projectRootPath, ProjectArtifactPaths.ManifestRelativePath)}");
        writer.WriteLine($"Source reference: {Path.Combine(projectRootPath, ProjectArtifactPaths.SourceReferenceRelativePath)}");
        writer.WriteLine($"Source media: {result.SourceReference.OriginalPath}");
        writer.WriteLine($"Normalized audio: {Path.Combine(projectRootPath, result.AudioArtifact.RelativePath)}");
        writer.WriteLine($"Waveform summary: {Path.Combine(projectRootPath, result.WaveformArtifact.RelativePath)}");
        writer.WriteLine($"Audio duration: {FormatDuration(result.AudioArtifact.DurationSeconds)}");
        writer.WriteLine($"Waveform duration: {FormatDuration(result.WaveformArtifact.DurationSeconds)}");
        writer.WriteLine($"Registered artifacts: {registeredArtifactCount}");
    }

    private static void WriteOpenSummary(
        TextWriter writer,
        string projectRootPath,
        OpenProjectResult result)
    {
        writer.WriteLine($"Project: {result.Project.Name}");
        writer.WriteLine($"Project root: {projectRootPath}");
        writer.WriteLine($"Database: {Path.Combine(projectRootPath, ProjectArtifactPaths.DatabaseFileName)}");
        writer.WriteLine($"Source status: {result.SourceStatus}");
        if (!string.IsNullOrWhiteSpace(result.SourceStatusMessage))
        {
            writer.WriteLine($"Source message: {result.SourceStatusMessage}");
        }

        if (result.SourceReference is not null)
        {
            writer.WriteLine($"Source path: {result.SourceReference.OriginalPath}");
        }

        if (result.MediaAsset is not null)
        {
            writer.WriteLine($"Media format: {result.MediaAsset.FormatName}");
            writer.WriteLine($"Media duration: {result.MediaAsset.DurationSeconds:F3} s");
        }

        writer.WriteLine($"Artifacts: {result.Artifacts.Count}");
        foreach (ProjectArtifact artifact in result.Artifacts)
        {
            writer.WriteLine($"  - {artifact.Kind}: {artifact.RelativePath} ({artifact.SizeBytes} bytes)");
        }
    }

    private static string FormatDuration(double? value) =>
        value is null ? "n/a" : $"{value.Value:F3} s";
}

public sealed record MediaIngestCommandOptions(
    string ProjectRootPath,
    string? ProjectName,
    string? SourceMediaPath,
    bool OpenExistingProject,
    string? FfmpegPath,
    string? FfprobePath,
    bool ShowHelp)
{
    public static bool TryParse(
        IReadOnlyList<string> args,
        TextWriter errorWriter,
        out MediaIngestCommandOptions options)
    {
        string? projectRootPath = null;
        string? projectName = null;
        string? sourceMediaPath = null;
        bool openExistingProject = false;
        string? ffmpegPath = null;
        string? ffprobePath = null;
        bool showHelp = false;

        for (int index = 0; index < args.Count; index++)
        {
            string arg = args[index];

            switch (arg)
            {
                case "--help":
                case "-h":
                case "/?":
                    showHelp = true;
                    break;

                case "--project":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out projectRootPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--name":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out projectName))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--media":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out sourceMediaPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--open":
                    openExistingProject = true;
                    break;

                case "--ffmpeg":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out ffmpegPath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                case "--ffprobe":
                    if (!TryReadValue(args, ref index, arg, errorWriter, out ffprobePath))
                    {
                        options = DefaultWithHelp();
                        return false;
                    }

                    break;

                default:
                    errorWriter.WriteLine($"Unknown argument '{arg}'.");
                    options = DefaultWithHelp();
                    return false;
            }
        }

        if (showHelp)
        {
            options = new MediaIngestCommandOptions(
                string.Empty,
                projectName,
                sourceMediaPath,
                openExistingProject,
                ffmpegPath,
                ffprobePath,
                ShowHelp: true);
            return true;
        }

        if (string.IsNullOrWhiteSpace(projectRootPath))
        {
            errorWriter.WriteLine("Missing required argument --project <path>.");
            options = DefaultWithHelp();
            return false;
        }

        if (openExistingProject)
        {
            if (!string.IsNullOrWhiteSpace(projectName) || !string.IsNullOrWhiteSpace(sourceMediaPath))
            {
                errorWriter.WriteLine("Do not combine --open with --name or --media.");
                options = DefaultWithHelp();
                return false;
            }

            options = new MediaIngestCommandOptions(
                Path.GetFullPath(projectRootPath),
                null,
                null,
                OpenExistingProject: true,
                NormalizePath(ffmpegPath),
                NormalizePath(ffprobePath),
                ShowHelp: false);
            return true;
        }

        if (string.IsNullOrWhiteSpace(projectName))
        {
            errorWriter.WriteLine("Missing required argument --name <project-name>.");
            options = DefaultWithHelp();
            return false;
        }

        if (string.IsNullOrWhiteSpace(sourceMediaPath))
        {
            errorWriter.WriteLine("Missing required argument --media <path>.");
            options = DefaultWithHelp();
            return false;
        }

        options = new MediaIngestCommandOptions(
            Path.GetFullPath(projectRootPath),
            projectName.Trim(),
            Path.GetFullPath(sourceMediaPath),
            OpenExistingProject: false,
            NormalizePath(ffmpegPath),
            NormalizePath(ffprobePath),
            ShowHelp: false);
        return true;
    }

    private static MediaIngestCommandOptions DefaultWithHelp() =>
        new(
            string.Empty,
            null,
            null,
            OpenExistingProject: false,
            null,
            null,
            ShowHelp: true);

    private static bool TryReadValue(
        IReadOnlyList<string> args,
        ref int index,
        string optionName,
        TextWriter errorWriter,
        out string value)
    {
        if (index + 1 >= args.Count)
        {
            errorWriter.WriteLine($"Missing value for {optionName}.");
            value = string.Empty;
            return false;
        }

        value = args[++index];
        return true;
    }

    private static string? NormalizePath(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : Path.GetFullPath(value);
}

public interface IMediaIngestCommandRunner
{
    Task<CreateProjectFromMediaResult> CreateAsync(MediaIngestCommandOptions options, CancellationToken cancellationToken);

    Task<OpenProjectResult> OpenAsync(MediaIngestCommandOptions options, CancellationToken cancellationToken);
}

public sealed class DefaultMediaIngestCommandRunner : IMediaIngestCommandRunner
{
    public async Task<CreateProjectFromMediaResult> CreateAsync(MediaIngestCommandOptions options, CancellationToken cancellationToken)
    {
        string databasePath = Path.Combine(options.ProjectRootPath, ProjectArtifactPaths.DatabaseFileName);
        string manifestPath = Path.Combine(options.ProjectRootPath, ProjectArtifactPaths.ManifestRelativePath);
        if (File.Exists(databasePath) || File.Exists(manifestPath))
        {
            throw new InvalidOperationException(
                $"Project path '{options.ProjectRootPath}' already contains Trackdub artifacts. Use '--open' to inspect the existing project.");
        }

        if (string.IsNullOrWhiteSpace(options.ProjectName) || string.IsNullOrWhiteSpace(options.SourceMediaPath))
        {
            throw new ArgumentException("Create mode requires both project name and source media path.");
        }

        ProjectMediaIngestService service = CreateService(options.ProjectRootPath, options.FfmpegPath, options.FfprobePath);
        return await service.CreateAsync(
            new CreateProjectFromMediaRequest(options.ProjectName, options.SourceMediaPath),
            cancellationToken).ConfigureAwait(false);
    }

    public async Task<OpenProjectResult> OpenAsync(MediaIngestCommandOptions options, CancellationToken cancellationToken)
    {
        ProjectMediaIngestService service = CreateService(options.ProjectRootPath, options.FfmpegPath, options.FfprobePath);
        return await service.OpenAsync(cancellationToken).ConfigureAwait(false);
    }

    private static ProjectMediaIngestService CreateService(
        string projectRootPath,
        string? ffmpegPath,
        string? ffprobePath)
    {
        var database = new SqliteProjectDatabase(projectRootPath);
        return new ProjectMediaIngestService(
            new SqliteProjectRepository(database),
            new SqliteMediaAssetRepository(database),
            new FileSystemArtifactStore(projectRootPath),
            new FfmpegMediaProbe(ffmpegPath, ffprobePath),
            new FfmpegAudioExtractionService(ffmpegPath),
            new WaveformSummaryGenerator(),
            new Sha256FileFingerprintService(),
            new PhysicalFileSystemProbe());
    }
}
