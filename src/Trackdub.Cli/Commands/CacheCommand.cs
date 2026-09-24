using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class CacheCommand
{
    public static Command Create()
    {
        var cacheCommand = new Command("cache", """
            Maintain local Trackdub cache directories.

            Examples:
              trackdub cache clear engines
            """);

        var clearCommand = new Command("clear", "Remove generated cache artifacts.");
        clearCommand.Add(CreateClearEnginesCommand());
        cacheCommand.Add(clearCommand);
        cacheCommand.Add(CreateWarmCommand());

        return cacheCommand;
    }

    private static Command CreateWarmCommand()
    {
        var modelOption = new Option<string[]>("--model")
        {
            Description = "Optional source ONNX path(s) to warm. Omit to scan the local model cache.",
            AllowMultipleArgumentsPerToken = true,
        };
        var command = new Command("warm", """
            Precompile EP-context models and warm the TensorRT RTX runtime cache.

            Run after install, app update, GPU swap, driver change, or TensorRT RTX EP bump.
            Produces *.epc.onnx next to each local ONNX graph and fills nv_runtime_cache_path
            so first inference does not pay JIT compile.

            Examples:
              trackdub cache warm
              trackdub cache warm --model path\to\sortformer.onnx
            """);
        command.Options.Add(modelOption);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            string[]? models = parseResult.GetValue(modelOption);
            IReadOnlyList<string>? modelPaths = models is { Length: > 0 } ? models : null;

            using (factory)
            {
                return await CacheHandler.WarmEnginesAsync(factory, Console.Out, modelPaths, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }

    private static Command CreateClearEnginesCommand()
    {
        var command = new Command("engines", """
            Delete TensorRT / ONNX engine runtime cache files.

            Safe after GPU or driver changes, or when bumping the TensorRT RTX EP bundle version.
            Does not remove model downloads or provider bundles.

            Examples:
              trackdub cache clear engines
            """);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await CacheHandler.ClearEnginesAsync(factory, Console.Out, cancellationToken)
                    .ConfigureAwait(false);
            }
        });

        return command;
    }
}
