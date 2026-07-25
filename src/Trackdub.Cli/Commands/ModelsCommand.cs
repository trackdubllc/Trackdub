using System.CommandLine;
using System.CommandLine.Parsing;
using System.Text.Json;

using Trackdub.Cli.Handlers;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Domain;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// <c>models download</c>, <c>status</c>, <c>verify</c>, and <c>bundle-needed</c> for headless model cache management.
/// </summary>
internal static class ModelsCommand
{
    public static Command Create()
    {
        var modelsCommand = new Command("models", """
            Download and inspect cached inference models.

            Examples:
              trackdub models status
              trackdub models status --json --missing-only
              trackdub models download onnx-community/silero-vad
              trackdub models verify
              trackdub models bundle-needed
            """);

        modelsCommand.Add(CreateDownloadCommand());
        modelsCommand.Add(CreateStatusCommand());
        modelsCommand.Add(CreateVerifyCommand());
        modelsCommand.Add(CreateBundleNeededCommand());

        return modelsCommand;
    }

    private static Command CreateDownloadCommand()
    {
        var modelIdArgument = new Argument<string?>("model-id")
        {
            Description = "Manifest model id or alias (omit with --all-missing to download all missing commercial bundled models)",
            Arity = ArgumentArity.ZeroOrOne,
        };

        var allMissingOption = new Option<bool>("--all-missing")
        {
            Description = "Download all missing commercial bundled models",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("download", """
            Download a model into the local cache.

            Examples:
              trackdub models download onnx-community/silero-vad
              trackdub models download --all-missing
            """)
        {
            modelIdArgument,
            allMissingOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool allMissing = parseResult.GetValue(allMissingOption);
            string? modelId = parseResult.GetValue(modelIdArgument);

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                if (allMissing)
                {
                    return await ModelsHandler.DownloadAllMissingAsync(factory, cancellationToken).ConfigureAwait(false);
                }

                if (string.IsNullOrWhiteSpace(modelId))
                {
                    CliErrorReporter.ReportValidationError(
                        ErrorCode.InvalidArgument,
                        "Provide a model-id or pass --all-missing.",
                        "model-id");
                    return Program.ExitArgumentError;
                }

                var progress = new Progress<ModelDownloadProgress>(report =>
                {
                    if (report.TotalBytes is > 0)
                    {
                        double percent = report.PercentComplete > 0
                            ? report.PercentComplete
                            : 100.0 * report.BytesDownloaded / report.TotalBytes.Value;
                        Console.Error.WriteLine(
                            $"Downloading {modelId}: {percent:F1}% ({report.BytesDownloaded}/{report.TotalBytes} bytes)");
                    }
                    else if (!string.IsNullOrWhiteSpace(report.Message))
                    {
                        Console.Error.WriteLine($"{modelId}: {report.Message}");
                    }
                });

                ModelDownloadResult result = await ModelsHandler
                    .DownloadModelAsync(factory, modelId, progress, cancellationToken)
                    .ConfigureAwait(false);

                if (!result.Success)
                {
                    CliErrorReporter.ReportError(
                        ErrorCode.ModelNotAvailable,
                        result.FailureReason ?? $"Download failed for '{modelId}'.");
                    return Program.ExitPipelineFailure;
                }

                Console.WriteLine($"Downloaded {modelId} ({result.NewState}).");
                return Program.ExitSuccess;
            }
        });

        return command;
    }

    private static Command CreateStatusCommand()
    {
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit machine-readable JSON",
            DefaultValueFactory = _ => false,
        };

        var filterOption = new Option<string?>("--filter")
        {
            Description = "Filter by stage:asr or model id/display name substring",
        };

        var missingOnlyOption = new Option<bool>("--missing-only")
        {
            Description = "Show only models that are not Ready or Installed",
            DefaultValueFactory = _ => false,
        };

        var command = new Command("status", """
            List manifest models and local cache state.

            Examples:
              trackdub models status
              trackdub models status --json --missing-only
              trackdub models status --filter stage:asr
            """)
        {
            jsonOption,
            filterOption,
            missingOnlyOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool emitJson = parseResult.GetValue(jsonOption);
            string? filter = parseResult.GetValue(filterOption);
            bool missingOnly = parseResult.GetValue(missingOnlyOption);

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                IModelInventoryService inventory =
                    factory.GetRequiredService<IModelInventoryService>();

                IReadOnlyList<ModelInventoryEntry> entries = await inventory
                    .GetAllAsync(cancellationToken)
                    .ConfigureAwait(false);

                IReadOnlyList<ModelInventoryEntry> filtered = ModelsHandler.FilterStatusEntries(
                    entries,
                    filter,
                    missingOnly);

                if (emitJson)
                {
                    var payload = filtered
                        .Select(entry => new ModelStatusRow
                        {
                            ModelId = entry.ModelId,
                            DisplayName = entry.DisplayName,
                            Task = entry.Task,
                            State = entry.State,
                            CanAutoDownload = entry.CanAutoDownload,
                            FailureReason = entry.FailureReason,
                        })
                        .ToList();

                    Console.WriteLine(JsonSerializer.Serialize(payload, CliJsonOptions.Default));
                }
                else
                {
                    foreach (ModelInventoryEntry entry in filtered.OrderBy(e => e.ModelId, StringComparer.OrdinalIgnoreCase))
                    {
                        Console.WriteLine($"{entry.ModelId}\t{entry.State}\t{entry.DisplayName}");
                    }
                }

                bool allReady = filtered.All(e => e.State is ModelCacheState.Ready or ModelCacheState.Installed);
                return allReady ? Program.ExitSuccess : Program.ExitPipelineFailure;
            }
        });

        return command;
    }

    private static Command CreateVerifyCommand()
    {
        var modelIdOption = new Option<string?>("--model-id")
        {
            Description = "Verify one model id. When omitted, verifies all manifest models.",
        };

        var command = new Command("verify", """
            Verify cached model files against manifest checksums.

            Examples:
              trackdub models verify
              trackdub models verify --model-id onnx-community/silero-vad
            """)
        {
            modelIdOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await ModelsHandler.VerifyAsync(
                    factory,
                    parseResult.GetValue(modelIdOption),
                    Console.Out,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateBundleNeededCommand()
    {
        var command = new Command("bundle-needed", """
            List default pipeline models that still need download or remediation.

            Examples:
              trackdub models bundle-needed
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
                return await ModelsHandler.BundleNeededAsync(factory, Console.Out, cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    private sealed class ModelStatusRow
    {
        public string? ModelId { get; init; }
        public string? DisplayName { get; init; }
        public string? Task { get; init; }
        public ModelCacheState State { get; init; }
        public bool CanAutoDownload { get; init; }
        public string? FailureReason { get; init; }
    }
}
