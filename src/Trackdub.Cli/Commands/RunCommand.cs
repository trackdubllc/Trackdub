using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Interactive;
using Trackdub.Cli.Handlers;
using Trackdub.Contracts;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class RunCommand
{
    private static readonly string[] AcceptedStageNames =
    [
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Asr,
        StageNames.Diarization,
        StageNames.Translation,
        StageNames.Tts,
        StageNames.LipSync,
        StageNames.Export,
        StageNames.LipSynthesis,
    ];

    public static Command Create(Func<bool>? isSetupInteractive = null)
    {
        Func<bool> setupInteractive = isSetupInteractive ?? (() => !Console.IsInputRedirected);

        var runCommand = new Command("run", """
            Unified pipeline execution API.

            Examples:
              trackdub run stage --project ./sample.trackdub --stage vad
              trackdub run pipeline --media ./video.mp4 --target-language es
            """);

        runCommand.Add(CreateStageCommand());
        runCommand.Add(CreatePipelineCommand(setupInteractive));

        return runCommand;
    }

    private static Command CreateStageCommand()
    {
        var projectOption = new Option<string>("--project")
        {
            Description = "Path to an existing .trackdub project directory",
            Required = true,
        };

        var stageOption = new Option<string>("--stage")
        {
            Description = "Canonical stage name to execute",
            Required = true,
        };
        stageOption.AcceptOnlyFromAmong(AcceptedStageNames);

        var modelOption = new Option<string?>("--model")
        {
            Description = "Model alias override for the target stage",
        };

        var command = new Command("stage", """
            Execute a single named pipeline stage against an existing project.

            Examples:
              trackdub run stage --project ./sample.trackdub --stage vad
              trackdub run stage --project ./sample.trackdub --stage asr --model whisper-small
            """)
        {
            projectOption,
            stageOption,
            modelOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            string progressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

            using (factory)
            {
                return await CliProgressRunner.ExecuteAsync(
                    progressFormat,
                    async (progress, ct) => await RunStageHandler.ExecuteAsync(
                        factory,
                        parseResult.GetValue(projectOption)!,
                        parseResult.GetValue(stageOption)!,
                        parseResult.GetValue(modelOption),
                        progress,
                        Console.Out,
                        ct).ConfigureAwait(false),
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreatePipelineCommand(Func<bool> setupInteractive)
    {
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
            Description = "Output directory for the project. Defaults to <media-stem>.trackdub adjacent to source media",
        };

        var modelOption = new Option<string[]>("--model")
        {
            Description = "Stage-specific model override in format stage:alias (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };
        modelOption.Arity = ArgumentArity.ZeroOrMore;

        var exportFormatOption = new Option<string?>("--export-format")
        {
            Description = "Export container format (mp4 or mkv)",
        };
        exportFormatOption.AcceptOnlyFromAmong("mp4", "mkv");

        var fromStageOption = new Option<string?>("--from-stage")
        {
            Description = "Run from this stage through export in canonical order",
        };
        fromStageOption.AcceptOnlyFromAmong(AcceptedStageNames);

        var onlyOption = new Option<string[]>("--only")
        {
            Description = "Run only the listed stages (repeatable)",
            AllowMultipleArgumentsPerToken = true,
        };
        onlyOption.Arity = ArgumentArity.ZeroOrMore;

        var forceRerunOption = new Option<bool>("--force-rerun")
        {
            Description = "Re-run stages even when valid artifacts already exist",
            DefaultValueFactory = _ => false,
        };

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
            Description = "Glob pattern for batch processing (resolved relative to current directory)",
        };

        var recursiveOption = new Option<bool>("--recursive")
        {
            Description = "Discover media files recursively (only valid with --input-dir)",
            DefaultValueFactory = _ => false,
        };

        var continueOnErrorOption = new Option<bool>("--continue-on-error")
        {
            Description = "Continue processing remaining files after a failure in batch mode",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("pipeline", """
            Execute a full or partial dubbing pipeline.

            Examples:
              trackdub run pipeline --media ./video.mp4 --target-language es
              trackdub run pipeline --media ./video.mp4 --target-language fr --from-stage translation
              trackdub run pipeline --media ./video.mp4 --target-language de --only vad --only asr --force-rerun
              trackdub run pipeline --input-dir ./videos --target-language es --continue-on-error
              trackdub run pipeline --preset my-preset --input-glob "**/*.mp4"
            """)
        {
            mediaOption,
            targetLanguageOption,
            sourceLanguageOption,
            outputOption,
            modelOption,
            exportFormatOption,
            fromStageOption,
            onlyOption,
            forceRerunOption,
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
            string? fromStage = parseResult.GetValue(fromStageOption);
            string[] onlyStages = parseResult.GetValue(onlyOption) ?? [];
            bool forceRerun = parseResult.GetValue(forceRerunOption);
            bool? enableAsrTextRefinement = parseResult.GetValue(enableAsrTextRefinementOption);
            string? presetName = parseResult.GetValue(presetOption);
            string? inputDir = parseResult.GetValue(inputDirOption);
            string? inputGlob = parseResult.GetValue(inputGlobOption);
            bool recursive = parseResult.GetValue(recursiveOption);
            bool continueOnError = parseResult.GetValue(continueOnErrorOption);

            if (!CliBatchCommandHelpers.TryValidateBatchInputOptions(
                    mediaPath,
                    inputDir,
                    inputGlob,
                    recursive,
                    out int batchValidationExitCode))
            {
                return batchValidationExitCode;
            }

            if (!CliBatchCommandHelpers.TryValidatePresetName(presetName, out int presetNameExitCode))
            {
                return presetNameExitCode;
            }

            bool isBatchMode = inputDir is not null || inputGlob is not null;

            // --- Batch execution path ---
            if (isBatchMode)
            {
                TrackdubSessionFactory? batchFactory = CliParseHelpers.TryBuildFactory(parseResult, out int batchBuildExitCode);
                if (batchFactory is null)
                {
                    return batchBuildExitCode;
                }

                string batchProgressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

                PipelinePreset? batchPreset = null;
                string? batchExecutionProvider;
                string? batchDevicePolicy;
                string? batchModelDirectory;
                using (batchFactory)
                {
                    if (presetName is not null)
                    {
                        (batchPreset, int loadExitCode) = await CliBatchCommandHelpers.TryLoadPresetAsync(
                            presetName,
                            batchFactory,
                            cancellationToken).ConfigureAwait(false);
                        if (loadExitCode != Program.ExitSuccess)
                        {
                            return loadExitCode;
                        }
                    }

                    // Resolve execution provider / device policy (explicit CLI > preset > default)
                    CliParseHelpers.ResolvePresetExecutionPreferences(
                        parseResult, batchPreset, out batchExecutionProvider, out batchDevicePolicy);
                    batchModelDirectory = CliParseHelpers.GetGlobalOptionValue<string?>(parseResult, "model-directory");
                }
                TrackdubSessionFactory? batchExecFactory = CliParseHelpers.TryBuildFactory(
                    batchModelDirectory, batchExecutionProvider, batchDevicePolicy, out int batchExecExitCode);
                if (batchExecFactory is null)
                {
                    return batchExecExitCode;
                }

                using (batchExecFactory)
                {
                    // Merge preset values with explicit CLI flags (explicit > preset > default)
                    string? resolvedTargetLanguage = targetLanguage ?? batchPreset?.TargetLanguage;
                    string? resolvedSourceLanguage = sourceLanguage ?? batchPreset?.SourceLanguage;
                    string? resolvedExportFormat = exportFormat ?? batchPreset?.ExportFormat;
                    bool resolvedEnableAsrTextRefinement = enableAsrTextRefinement ?? batchPreset?.EnableAsrTextRefinement ?? false;
                    string[] resolvedModelOverrides = CliBatchCommandHelpers.ResolveModelOverrides(modelOverrides, batchPreset);

                    Dictionary<string, string>? batchModelPreferences = CliModelOverrides.Parse(resolvedModelOverrides);
                    if (batchModelPreferences is null)
                    {
                        return Program.ExitArgumentError;
                    }

                    // Validate target language required in batch mode
                    if (string.IsNullOrWhiteSpace(resolvedTargetLanguage))
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

                    // Build stage filter
                    IReadOnlyList<string>? batchStageFilter = CliStageFilter.Build(fromStage, onlyStages);
                    if (batchStageFilter is { Count: 0 })
                    {
                        return Program.ExitArgumentError;
                    }

                    // Build template DubbingSessionOptions
                    var templateOptions = new DubbingSessionOptions
                    {
                        SourceMediaPath = "batch",
                        ProjectOutputDirectory = null,
                        SourceLanguageCode = resolvedSourceLanguage,
                        TargetLanguageCode = resolvedTargetLanguage,
                        ModelPreferences = batchModelPreferences.Count > 0 ? batchModelPreferences : null,
                        ExportFormat = resolvedExportFormat,
                        StageFilter = batchStageFilter,
                        ForceRerun = forceRerun,
                        EnableAsrTextRefinement = resolvedEnableAsrTextRefinement,
                    };

                    // Build BatchOptions
                    string? resolvedOutputRoot = outputDirectory is not null
                        ? Path.GetFullPath(outputDirectory)
                        : null;

                    var batchOptions = new BatchOptions
                    {
                        ContinueOnError = continueOnError,
                        OutputRoot = resolvedOutputRoot,
                    };

                    return await BatchHandler.ExecuteAsync(
                        batchExecFactory,
                        mediaFiles,
                        templateOptions,
                        batchOptions,
                        presetName,
                        batchProgressFormat,
                        Console.Out,
                        cancellationToken).ConfigureAwait(false);
                }
            }

            // --- Single-file / pipeline-resume path (existing behavior) ---

            // If --preset provided in single-file mode, resolve and merge
            PipelinePreset? preset = null;
            if (presetName is not null)
            {
                (preset, int presetExitCode) = await CliBatchCommandHelpers.TryLoadPresetAsync(
                    presetName,
                    parseResult,
                    cancellationToken).ConfigureAwait(false);
                if (presetExitCode != Program.ExitSuccess)
                {
                    return presetExitCode;
                }

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
            }

            IReadOnlyList<string>? stageFilter = CliStageFilter.Build(fromStage, onlyStages);
            if (stageFilter is { Count: 0 })
            {
                return Program.ExitArgumentError;
            }

            bool requiresSourceMedia = stageFilter is null
                || stageFilter.Any(TrackdubPipelineStages.RequiresSourceMedia);

            string? resolvedOutputDirectory = outputDirectory is not null
                ? Path.GetFullPath(outputDirectory)
                : null;

            bool projectResume = !requiresSourceMedia
                && resolvedOutputDirectory is not null
                && TrackdubProjectPaths.ContainsDatabase(resolvedOutputDirectory);

            var setupRequest = new DubSetupRequest(
                mediaPath,
                targetLanguage,
                sourceLanguage,
                outputDirectory,
                modelOverrides,
                exportFormat);

            if (!projectResume && DubSetupWizard.RequiresSetup(setupRequest))
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

                if (outputDirectory is not null)
                {
                    resolvedOutputDirectory = Path.GetFullPath(outputDirectory);
                }
            }

            Dictionary<string, string>? modelPreferences = CliModelOverrides.Parse(modelOverrides);
            if (modelPreferences is null)
            {
                return Program.ExitArgumentError;
            }

            // Resolve execution provider / device policy (explicit CLI > preset > default)
            CliParseHelpers.ResolvePresetExecutionPreferences(
                parseResult, preset, out string? resolvedExecutionProvider, out string? resolvedDevicePolicy);
            string? modelDirectory = CliParseHelpers.GetGlobalOptionValue<string?>(parseResult, "model-directory");

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(
                modelDirectory, resolvedExecutionProvider, resolvedDevicePolicy, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            string progressFormat = CliParseHelpers.GetGlobalOptionValue<string>(parseResult, "progress") ?? "text";

            using (factory)
            {
                TrackdubProjectContext? projectContext = null;
                if (resolvedOutputDirectory is not null
                    && TrackdubProjectPaths.ContainsDatabase(resolvedOutputDirectory))
                {
                    projectContext = await TrackdubProjectContextResolver.TryOpenAsync(
                        factory,
                        resolvedOutputDirectory,
                        cancellationToken).ConfigureAwait(false);
                }

                if (projectResume)
                {
                    if (projectContext is null)
                    {
                        CliErrorReporter.ReportValidationError(
                            ErrorCode.InvalidArgument,
                            $"Existing project not found or unreadable: {resolvedOutputDirectory}",
                            "--output");
                        return Program.ExitArgumentError;
                    }

                    if (string.IsNullOrWhiteSpace(mediaPath))
                    {
                        mediaPath = projectContext.SourceMediaPath ?? string.Empty;
                    }

                    if (string.IsNullOrWhiteSpace(targetLanguage))
                    {
                        targetLanguage = projectContext.TargetLanguageCode;
                    }
                }
                else if (string.IsNullOrWhiteSpace(mediaPath) || string.IsNullOrWhiteSpace(targetLanguage))
                {
                    ReportMissingSetupValues(setupRequest);
                    return Program.ExitArgumentError;
                }

                string resolvedMediaPath = string.IsNullOrWhiteSpace(mediaPath)
                    ? string.Empty
                    : Path.GetFullPath(mediaPath);

                if (requiresSourceMedia)
                {
                    if (!File.Exists(resolvedMediaPath)
                        && !string.IsNullOrWhiteSpace(projectContext?.SourceMediaPath))
                    {
                        resolvedMediaPath = Path.GetFullPath(projectContext.SourceMediaPath);
                    }

                    if (!File.Exists(resolvedMediaPath))
                    {
                        CliErrorReporter.ReportValidationError(
                            ErrorCode.MediaNotFound,
                            $"Media file not found: {resolvedMediaPath}",
                            "--media");
                        return Program.ExitArgumentError;
                    }
                }

                if (resolvedOutputDirectory is null)
                {
                    if (string.IsNullOrWhiteSpace(resolvedMediaPath))
                    {
                        CliErrorReporter.ReportValidationError(
                            ErrorCode.InvalidArgument,
                            "Option '--output' is required when resuming a project without source media.",
                            "--output");
                        return Program.ExitArgumentError;
                    }

                    resolvedOutputDirectory = Path.Combine(
                        Path.GetDirectoryName(resolvedMediaPath) ?? ".",
                        Path.GetFileNameWithoutExtension(resolvedMediaPath) + ".trackdub");
                }

                if (string.IsNullOrWhiteSpace(targetLanguage))
                {
                    CliErrorReporter.ReportValidationError(
                        ErrorCode.InvalidArgument,
                        "Option '--target-language' is required. Run this command in an interactive terminal for guided setup, or pass --target-language explicitly.",
                        "--target-language");
                    return Program.ExitArgumentError;
                }

                return await CliProgressRunner.ExecuteAsync(
                    progressFormat,
                    async (progress, ct) => await RunPipelineHandler.ExecuteAsync(
                        factory,
                        new RunPipelineHandler.RunPipelineRequest
                        {
                            SourceMediaPath = resolvedMediaPath,
                            ProjectOutputDirectory = resolvedOutputDirectory,
                            SourceLanguageCode = sourceLanguage,
                            TargetLanguageCode = targetLanguage,
                            ModelPreferences = modelPreferences.Count > 0 ? modelPreferences : null,
                            ExportFormat = exportFormat,
                            StageFilter = stageFilter,
                            ForceRerun = forceRerun,
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
