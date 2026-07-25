using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class ConfigCommand
{
    public static Command Create()
    {
        var command = new Command("config", """
            Report Trackdub cache directories, log file, manifest paths, and persisted settings.

            Examples:
              trackdub config paths
              trackdub config show
              trackdub config preset save my-preset --target-language es
              trackdub config preset list
            """);

        command.Add(CreatePathsCommand());
        command.Add(CreateShowCommand());
        command.Add(CreatePresetCommand());

        return command;
    }

    private static Command CreatePathsCommand()
    {
        var command = new Command("paths", """
            Emit filesystem paths for app data, model cache, tools, and bundled manifest.

            Examples:
              trackdub config paths
              trackdub config paths --model-directory ./models
            """);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ConfigHandler.PathsAsync(factory, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateShowCommand()
    {
        var command = new Command("show", """
            Emit paths plus persisted settings.json summary (recent projects and language defaults).

            Examples:
              trackdub config show
              trackdub config show --model-directory ./models
            """);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ConfigHandler.ShowAsync(factory, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreatePresetCommand()
    {
        var command = new Command("preset", """
            Manage named pipeline presets (save, load, list, delete).

            Examples:
              trackdub config preset save my-preset --target-language es --model asr:whisper-large-v3
              trackdub config preset load my-preset
              trackdub config preset list
              trackdub config preset delete my-preset
            """);

        command.Add(CreatePresetSaveCommand());
        command.Add(CreatePresetLoadCommand());
        command.Add(CreatePresetListCommand());
        command.Add(CreatePresetDeleteCommand());

        return command;
    }

    private static Command CreatePresetSaveCommand()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Name of the preset to save",
        };

        var targetLanguageOption = new Option<string>("--target-language")
        {
            Description = "Target language BCP-47 code (e.g., es, fr, de). Required: a preset cannot be saved without a valid target language.",
            Required = true,
        };

        var sourceLanguageOption = new Option<string?>("--source-language")
        {
            Description = "Source language BCP-47 code",
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

        var executionProviderOption = new Option<string?>("--execution-provider")
        {
            Description = "ONNX execution provider (e.g., auto, directml, cpu)",
        };

        var devicePolicyOption = new Option<string?>("--device-policy")
        {
            Description = $"Device selection policy (e.g., {WindowsMlExecutionDevicePolicySettings.FormatSupportedKeys()})",
        };

        var enableAsrTextRefinementOption = new Option<bool?>("--enable-asr-text-refinement")
        {
            Description = "Run optional ASR text refinement after transcription",
        };

        var command = new Command("save", """
            Save pipeline settings as a named preset.

            Examples:
              trackdub config preset save my-preset --target-language es
              trackdub config preset save gpu-fast --target-language fr --execution-provider directml --device-policy max-performance
              trackdub config preset save custom --target-language de --model asr:whisper-large-v3 --model tts:kokoro-onnx
            """)
        {
            nameArgument,
            targetLanguageOption,
            sourceLanguageOption,
            modelOption,
            exportFormatOption,
            executionProviderOption,
            devicePolicyOption,
            enableAsrTextRefinementOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string name = parseResult.GetValue(nameArgument)!;
            string targetLanguage = parseResult.GetValue(targetLanguageOption)!;
            if (string.IsNullOrWhiteSpace(targetLanguage))
            {
                await Console.Error.WriteLineAsync(
                    "A non-empty --target-language is required when saving a preset.")
                    .ConfigureAwait(false);
                return Program.ExitArgumentError;
            }

            string? sourceLanguage = parseResult.GetValue(sourceLanguageOption);
            string[] modelOverrides = parseResult.GetValue(modelOption) ?? [];
            string? exportFormat = parseResult.GetValue(exportFormatOption);
            string? executionProvider = parseResult.GetValue(executionProviderOption);
            string? devicePolicy = parseResult.GetValue(devicePolicyOption);
            bool? enableAsrTextRefinement = parseResult.GetValue(enableAsrTextRefinementOption);

            // Requirement 1.7: At least one pipeline setting flag must be provided.
            bool hasAnySetting = targetLanguage is not null
                || sourceLanguage is not null
                || modelOverrides.Length > 0
                || exportFormat is not null
                || executionProvider is not null
                || devicePolicy is not null
                || enableAsrTextRefinement is not null;

            if (!hasAnySetting)
            {
                await Console.Error.WriteLineAsync(
                    "At least one pipeline setting flag is required (e.g., --target-language, --model, --export-format).")
                    .ConfigureAwait(false);
                return Program.ExitArgumentError;
            }

            // Parse --model stage:alias pairs into dictionary.
            Dictionary<string, string>? models = null;
            if (modelOverrides.Length > 0)
            {
                models = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (string entry in modelOverrides)
                {
                    int colonIndex = entry.IndexOf(':');
                    if (colonIndex <= 0 || colonIndex >= entry.Length - 1)
                    {
                        await Console.Error.WriteLineAsync(
                            $"Invalid --model format '{entry}'. Expected 'stage:alias' (e.g., asr:whisper-large-v3).")
                            .ConfigureAwait(false);
                        return Program.ExitArgumentError;
                    }

                    string stage = entry[..colonIndex];
                    string alias = entry[(colonIndex + 1)..];
                    models[stage] = alias;
                }
            }

            var preset = new PipelinePreset
            {
                Version = PipelinePreset.CurrentVersion,
                TargetLanguage = targetLanguage!,
                SourceLanguage = sourceLanguage,
                Models = models,
                ExportFormat = exportFormat,
                ExecutionProvider = executionProvider,
                DevicePolicy = devicePolicy,
                EnableAsrTextRefinement = enableAsrTextRefinement,
            };

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
                string presetsDirectory = Path.Combine(storagePaths.RootDirectory, "presets");
                var store = new PresetStore(presetsDirectory);

                return await PresetHandler.SaveAsync(name, preset, store, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreatePresetLoadCommand()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Name of the preset to load",
        };

        var command = new Command("load", """
            Load and display the contents of a named preset.

            Examples:
              trackdub config preset load my-preset
            """)
        {
            nameArgument,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string name = parseResult.GetValue(nameArgument)!;

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
                string presetsDirectory = Path.Combine(storagePaths.RootDirectory, "presets");
                var store = new PresetStore(presetsDirectory);

                return await PresetHandler.LoadAsync(name, store, Console.Out, Console.Error, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreatePresetListCommand()
    {
        var command = new Command("list", """
            List all saved presets.

            Examples:
              trackdub config preset list
            """);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
                string presetsDirectory = Path.Combine(storagePaths.RootDirectory, "presets");
                var store = new PresetStore(presetsDirectory);

                return await PresetHandler.ListAsync(store, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreatePresetDeleteCommand()
    {
        var nameArgument = new Argument<string>("name")
        {
            Description = "Name of the preset to delete",
        };

        var command = new Command("delete", """
            Delete a named preset.

            Examples:
              trackdub config preset delete my-preset
            """)
        {
            nameArgument,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            string name = parseResult.GetValue(nameArgument)!;

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
                string presetsDirectory = Path.Combine(storagePaths.RootDirectory, "presets");
                var store = new PresetStore(presetsDirectory);

                return await PresetHandler.DeleteAsync(name, store, Console.Out, Console.Error, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }
}
