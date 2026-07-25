using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliBatchCommandHelpers
{
    internal static bool TryValidateBatchInputOptions(
        string? mediaPath,
        string? inputDir,
        string? inputGlob,
        bool recursive,
        out int exitCode)
    {
        exitCode = Program.ExitSuccess;

        if (inputDir is not null && inputGlob is not null)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "Options '--input-dir' and '--input-glob' are mutually exclusive. Specify one or the other.",
                "--input-dir");
            exitCode = Program.ExitArgumentError;
            return false;
        }

        if ((inputDir is not null || inputGlob is not null) && mediaPath is not null)
        {
            string conflictingOption = inputDir is not null ? "--input-dir" : "--input-glob";
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Options '{conflictingOption}' and '--media' are mutually exclusive. Use '--media' for single file or '{conflictingOption}' for batch processing.",
                conflictingOption);
            exitCode = Program.ExitArgumentError;
            return false;
        }

        if (recursive && inputDir is null)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "Option '--recursive' is only valid with '--input-dir'.",
                "--recursive");
            exitCode = Program.ExitArgumentError;
            return false;
        }

        return true;
    }

    internal static bool TryValidatePresetName(string? presetName, out int exitCode)
    {
        exitCode = Program.ExitSuccess;

        if (presetName is null || PresetNameValidator.IsValid(presetName))
        {
            return true;
        }

        CliErrorReporter.ReportValidationError(
            ErrorCode.InvalidArgument,
            $"Invalid preset name '{presetName}'. Names must contain only alphanumeric characters, hyphens, and underscores (1-64 characters).",
            "--preset");
        exitCode = Program.ExitArgumentError;
        return false;
    }

    internal static async Task<(PipelinePreset? Preset, int ExitCode)> TryLoadPresetAsync(
        string presetName,
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        string presetsDirectory = Path.Combine(storagePaths.RootDirectory, "presets");
        var store = new PresetStore(presetsDirectory);

        PipelinePreset? preset;
        try
        {
            preset = await store.LoadAsync(presetName, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or InvalidOperationException)
        {
            string filePath = Path.Combine(presetsDirectory, $"{presetName}.json");
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"Failed to read preset '{presetName}' at {filePath}: {ex.Message}",
                "--preset");
            return (null, Program.ExitArgumentError);
        }

        if (preset is not null)
        {
            return (preset, Program.ExitSuccess);
        }

        CliErrorReporter.ReportValidationError(
            ErrorCode.InvalidArgument,
            $"Preset '{presetName}' not found.",
            "--preset");
        return (null, Program.ExitArgumentError);
    }

    internal static async Task<(PipelinePreset? Preset, int ExitCode)> TryLoadPresetAsync(
        string? presetName,
        ParseResult parseResult,
        CancellationToken cancellationToken)
    {
        if (presetName is null)
        {
            return (null, Program.ExitSuccess);
        }

        if (!TryValidatePresetName(presetName, out int nameExitCode))
        {
            return (null, nameExitCode);
        }

        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
        if (factory is null)
        {
            return (null, buildExitCode);
        }

        using (factory)
        {
            return await TryLoadPresetAsync(presetName, factory, cancellationToken).ConfigureAwait(false);
        }
    }

    internal static bool TryDiscoverBatchMediaFiles(
        string? inputDir,
        string? inputGlob,
        bool recursive,
        out IReadOnlyList<string> mediaFiles,
        out int exitCode)
    {
        mediaFiles = [];
        exitCode = Program.ExitSuccess;

        try
        {
            if (inputDir is not null)
            {
                string resolvedDir = Path.GetFullPath(inputDir);
                if (!Directory.Exists(resolvedDir))
                {
                    CliErrorReporter.ReportValidationError(
                        ErrorCode.InvalidArgument,
                        $"Directory not found: {resolvedDir}",
                        "--input-dir");
                    exitCode = Program.ExitArgumentError;
                    return false;
                }

                mediaFiles = BatchFileDiscovery.FromDirectory(resolvedDir, recursive);
            }
            else
            {
                mediaFiles = BatchFileDiscovery.FromGlob(inputGlob!, Environment.CurrentDirectory);
            }
        }
        catch (DirectoryNotFoundException ex)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                ex.Message,
                inputDir is not null ? "--input-dir" : "--input-glob");
            exitCode = Program.ExitArgumentError;
            return false;
        }
        catch (InvalidOperationException ex)
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                ex.Message,
                inputDir is not null ? "--input-dir" : "--input-glob");
            exitCode = Program.ExitArgumentError;
            return false;
        }

        if (mediaFiles.Count == 0)
        {
            string source = inputDir is not null
                ? $"directory '{Path.GetFullPath(inputDir)}'"
                : $"glob pattern '{inputGlob}'";
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                $"No supported media files found in {source}.",
                inputDir is not null ? "--input-dir" : "--input-glob");
            exitCode = Program.ExitArgumentError;
            return false;
        }

        return true;
    }

    internal static void ApplyPresetPipelineDefaults(
        PipelinePreset preset,
        ref string? targetLanguage,
        ref string? sourceLanguage,
        ref string? exportFormat,
        ref bool? enableAsrTextRefinement)
    {
        targetLanguage ??= preset.TargetLanguage;
        sourceLanguage ??= preset.SourceLanguage;
        exportFormat ??= preset.ExportFormat;

        enableAsrTextRefinement ??= preset.EnableAsrTextRefinement;
    }

    internal static string[] ResolveModelOverrides(string[] explicitModelOverrides, PipelinePreset? preset)
    {
        if (preset?.Models is null || preset.Models.Count == 0)
        {
            return explicitModelOverrides;
        }

        var explicitModels = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string entry in explicitModelOverrides)
        {
            if (!TryParseModelOverride(entry, out string stage, out string alias))
            {
                return explicitModelOverrides;
            }

            explicitModels[stage] = alias;
        }

        if (explicitModels.Count == 0 && explicitModelOverrides.Length > 0)
        {
            return explicitModelOverrides;
        }

        var resolvedModels = new Dictionary<string, string>(preset.Models, StringComparer.OrdinalIgnoreCase);
        foreach (var kvp in explicitModels)
        {
            resolvedModels[kvp.Key] = kvp.Value;
        }

        return resolvedModels
            .Select(kvp => $"{kvp.Key}:{kvp.Value}")
            .ToArray();
    }

    private static bool TryParseModelOverride(string entry, out string stage, out string alias)
    {
        stage = string.Empty;
        alias = string.Empty;

        int colonIndex = entry.IndexOf(':');
        if (colonIndex <= 0 || colonIndex >= entry.Length - 1)
        {
            return false;
        }

        stage = entry[..colonIndex].Trim();
        alias = entry[(colonIndex + 1)..].Trim();
        return !string.IsNullOrEmpty(stage) && !string.IsNullOrEmpty(alias);
    }
}
