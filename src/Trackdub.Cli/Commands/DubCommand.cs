using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Interactive;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// Defines the <c>dub</c> CLI command that executes a full dubbing pipeline
/// from media ingest through export.
/// </summary>
internal static class DubCommand
{
    /// <summary>
    /// Creates the <c>dub</c> command with all options and the execution handler.
    /// </summary>
    public static Command Create(Func<bool>? isSetupInteractive = null)
    {
        Func<bool> setupInteractive = isSetupInteractive ?? (() => !Console.IsInputRedirected);

        var mediaOption = new Option<string?>("--media")
        {
            Description = "Path to the source media file",
        };

        var targetLanguageOption = new Option<string?>("--target-language")
        {
            Description = "Target language BCP-47 code (e.g., es, fr, de)",
        };

        var sourceLanguageOption = new Option<string?>("--source-language")
        {
            Description = "Source language BCP-47 code. When omitted, ASR auto-detects the source language",
        };

        var outputOption = new Option<string?>("--output")
        {
            Description = "Output directory for the project. Defaults to <media-stem>.trackdub/ adjacent to source media",
        };

        var modelOption = new Option<string[]>("--model")
        {
            Description = "Stage-specific model override in format stage:alias (repeatable, e.g., --model asr:large-v3 --model tts:xtts-v2)",
            AllowMultipleArgumentsPerToken = true,
        };
        modelOption.Arity = ArgumentArity.ZeroOrMore;

        var exportFormatOption = new Option<string?>("--export-format")
        {
            Description = "Export container format (mp4 or mkv)",
        };
        exportFormatOption.AcceptOnlyFromAmong("mp4", "mkv");

        var enableAsrTextRefinementOption = new Option<bool?>("--enable-asr-text-refinement")
        {
            Description = "Run optional Qwen ASR text polish after transcription",
        };

        var presetOption = new Option<string?>("--preset")
        {
            Description = "Named preset to load pipeline settings from",
        };

        var inputDirOption = new Option<string?>("--input-dir")
        {
            Description = "Directory path for batch processing of all supported media files",
        };

        var inputGlobOption = new Option<string?>("--input-glob")
        {
            Description = "Glob pattern for batch processing (resolved relative to current working directory)",
        };

        var recursiveOption = new Option<bool>("--recursive")
        {
            Description = "Discover media files recursively in subdirectories (only with --input-dir)",
            DefaultValueFactory = _ => false,
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Continue processing remaining files after a failure in batch mode",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("dub", """
            Execute a full dubbing pipeline from media ingest through export.

            Examples:
              trackdub dub --media ./video.mp4 --target-language es
              trackdub dub --media ./video.mp4 --target-language fr --export-format mkv
              trackdub dub --media ./video.mp4 --target-language de --model asr:whisper-small --model tts:kokoro-onnx
              trackdub dub --preset my-preset --input-dir ./videos
              trackdub dub --preset my-preset --input-glob "**/*.mp4"
            """)
        {
            mediaOption,
            targetLanguageOption,
            sourceLanguageOption,
            outputOption,
            modelOption,
            exportFormatOption,
            enableAsrTextRefinementOption,
            presetOption,
            inputDirOption,
            inputGlobOption,
            recursiveOption,
            continueOnErrorOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string? mediaPath = parseResult.GetValue(mediaOption);
            string? targetLanguage = parseResult.GetValue(targetLanguageOption);
            string? sourceLanguage = parseResult.GetValue(sourceLanguageOption);
            string? outputDirectory = parseResult.GetValue(outputOption);
            string[] modelOverrides = parseResult.GetValue(modelOption) ?? [];
            string? exportFormat = parseResult.GetValue(exportFormatOption);
            bool? enableAsrTextRefinement = parseResult.GetValue(enableAsrTextRefinementOption);
            string? presetName = parseResult.GetValue(presetOption);
            string? inputDir = parseResult.GetValue(inputDirOption);
            string? inputGlob = parseResult.GetValue(inputGlobOption);
            bool recursive = parseResult.GetValue(recursiveOption);
            bool continueOnError = parseResult.GetValue(continueOnErrorOption);

            bool isBatchMode = inputDir is not null || inputGlob is not null;

            if (!CliBatchCommandHelpers.TryValidateBatchInputOptions(
                    mediaPath,
                    inputDir,
                    inputGlob,
                    recursive,
                    out int batchValidationExitCode))
            {
                return batchValidationExitCode;
            }

            // --- Preset resolution ---

            (PipelinePreset? preset, int presetExitCode) = await CliBatchCommandHelpers.TryLoadPresetAsync(
                presetName,
                parseResult,
                cancellationToken).ConfigureAwait(false);
            if (presetExitCode != Program.ExitSuccess)
            {
                return presetExitCode;
            }

            // --- Merge preset values with explicit flags (explicit > preset > default) ---

            if (preset is not null)
            {
                CliBatchCommandHelpers.ApplyPresetPipelineDefaults(
                    preset,
                    ref targetLanguage,
                    ref sourceLanguage,
                    ref exportFormat,
                    ref enableAsrTextRefinement);
                modelOverrides = CliBatchCommandHelpers.ResolveModelOverrides(modelOverrides, preset);
            }

            // Resolve execution provider / device policy (explicit CLI > preset > default)
            CliParseHelpers.ResolvePresetExecutionPreferences(
                parseResult, preset, out string? resolvedExecutionProvider, out string? resolvedDevicePolicy);
            string? modelDirectory = CliParseHelpers.GetGlobalOptionValue<string?>(parseResult, "model-directory");

            // --- Batch processing path ---

            if (isBatchMode)
            {
                // In batch mode, target-language is required (from preset or explicit)
                if (string.IsNullOrWhiteSpace(targetLanguage))
                {
                    CliErrorReporter.ReportValidationError(
                        ErrorCode.InvalidArgument,
                        "Option '--target-language' is required for batch processing. Provide it explicitly or via a preset.",
                        "--target-language");
                    return Program.ExitArgumentError;
                }

                // Discover files
                if (!CliBatchCommandHelpers.TryDiscoverBatchMediaFiles(
                        inputDir,
                        inputGlob,
                        recursive,
                        out IReadOnlyList<string> mediaFiles,
                        out int discoveryExitCode))
                {
                    return discoveryExitCode;
                }

                // Parse model overrides
                Dictionary<string, string>? modelPreferences = CliModelOverrides.Parse(modelOverrides);
                if (modelPreferences is null)
                {
                    return Program.ExitArgumentError;
                }

                // Build template DubbingSessionOptions (SourceMediaPath is a placeholder; BatchProcessor overrides per-file)
                var templateOptions = new DubbingSessionOptions
                {
                    SourceMediaPath = "batch",
                    TargetLanguageCode = targetLanguage,
                    SourceLanguageCode = sourceLanguage,
                    ProjectOutputDirectory = outputDirectory is not null ? Path.GetFullPath(outputDirectory) : null,
                    ModelPreferences = modelPreferences.Count > 0 ? modelPreferences : null,
                    ExportFormat = exportFormat,
                    EnableAsrTextRefinement = enableAsrTextRefinement ?? false,
                };

                // Build BatchOptions
                var batchOptions = new BatchOptions
                {
                    ContinueOnError = continueOnError,
                    OutputRoot = outputDirectory is not null ? Path.GetFullPath(outputDirectory) : null,
                };

                // Build factory for batch execution
                TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(
                    modelDirectory, resolvedExecutionProvider, resolvedDevicePolicy, out int buildExitCode);
                if (factory is null)
                {
                    return buildExitCode;
                }

                string progressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

                using (factory)
                {
                    return await BatchHandler.ExecuteAsync(
                        factory,
                        mediaFiles,
                        templateOptions,
                        batchOptions,
                        presetName,
                        progressFormat,
                        Console.Out,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            // --- Single-file path (existing behavior) ---

            var setupRequest = new DubSetupRequest(
                mediaPath,
                targetLanguage,
                sourceLanguage,
                outputDirectory,
                modelOverrides,
                exportFormat);

            if (DubSetupWizard.RequiresSetup(setupRequest))
            {
                if (!setupInteractive())
                {
                    ReportMissingSetupValues(setupRequest);
                    return Program.ExitArgumentError;
                }

                DubSetupRequest? completedSetup = await DubSetupWizard
                    .CompleteAsync(setupRequest, new SpectreDubSetupPromptAdapter(), cancellationToken)
                    .ConfigureAwait(false);

                if (completedSetup is null)
                {
                    CliErrorReporter.ReportError(ErrorCode.Cancelled, "Interactive setup was cancelled before required inputs were collected.");
                    return Program.ExitArgumentError;
                }

                setupRequest = completedSetup;
                mediaPath = setupRequest.MediaPath;
                targetLanguage = setupRequest.TargetLanguage;
                sourceLanguage = setupRequest.SourceLanguage;
                outputDirectory = setupRequest.OutputDirectory;
                modelOverrides = setupRequest.ModelOverrides;
                exportFormat = setupRequest.ExportFormat;
            }

            if (string.IsNullOrWhiteSpace(mediaPath) || string.IsNullOrWhiteSpace(targetLanguage))
            {
                ReportMissingSetupValues(setupRequest);
                return Program.ExitArgumentError;
            }

            string resolvedMediaPath = Path.GetFullPath(mediaPath);
            if (!File.Exists(resolvedMediaPath))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.MediaNotFound,
                    $"Media file not found: {resolvedMediaPath}",
                    "--media");
                return Program.ExitArgumentError;
            }

            string resolvedOutputDirectory = outputDirectory is not null
                ? Path.GetFullPath(outputDirectory)
                : Path.Combine(
                    Path.GetDirectoryName(resolvedMediaPath) ?? ".",
                    Path.GetFileNameWithoutExtension(resolvedMediaPath) + ".trackdub");

            Dictionary<string, string>? singleModelPreferences = CliModelOverrides.Parse(modelOverrides);
            if (singleModelPreferences is null)
            {
                return Program.ExitArgumentError;
            }

            TrackdubSessionFactory? singleFactory = CliParseHelpers.TryBuildFactory(
                modelDirectory, resolvedExecutionProvider, resolvedDevicePolicy, out int singleBuildExitCode);
            if (singleFactory is null)
            {
                return singleBuildExitCode;
            }

            string singleProgressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

            using (singleFactory)
            {
                return await CliProgressRunner.ExecuteAsync(
                    singleProgressFormat,
                    async (progress, ct) => await RunPipelineHandler.ExecuteAsync(
                        singleFactory,
                        new RunPipelineHandler.RunPipelineRequest
                        {
                            SourceMediaPath = resolvedMediaPath,
                            ProjectOutputDirectory = resolvedOutputDirectory,
                            SourceLanguageCode = sourceLanguage,
                            TargetLanguageCode = targetLanguage,
                            ModelPreferences = singleModelPreferences.Count > 0 ? singleModelPreferences : null,
                            ExportFormat = exportFormat,
                            EnableAsrTextRefinement = enableAsrTextRefinement ?? false,
                        },
                        progress,
                        Console.Out,
                        ct).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static void ReportMissingSetupValues(DubSetupRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.MediaPath))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "Option '--media' is required. Run this command in an interactive terminal for guided setup, or pass --media explicitly.",
                "--media");
        }

        if (string.IsNullOrWhiteSpace(request.TargetLanguage))
        {
            CliErrorReporter.ReportValidationError(
                ErrorCode.InvalidArgument,
                "Option '--target-language' is required. Run this command in an interactive terminal for guided setup, or pass --target-language explicitly.",
                "--target-language");
        }
    }
}
