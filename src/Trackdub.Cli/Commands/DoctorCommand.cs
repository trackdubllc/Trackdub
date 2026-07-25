using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class DoctorCommand
{
    public static Command Create()
    {
        var jsonOption = new Option<bool>("--json")
        {
            Description = "Emit machine-readable JSON (default output is JSON)",
            DefaultValueFactory = _ => true,
        };

        var command = new Command("doctor", """
            Operator health checks for model cache, FFmpeg, playback natives, and pipeline readiness.

            Examples:
              trackdub doctor
              trackdub doctor --json
              trackdub doctor --model-directory ./models
            """)
        {
            jsonOption,
        };

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
                return await DoctorHandler.ExecuteAsync(factory, Console.Out, cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }
}
