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

        return cacheCommand;
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
