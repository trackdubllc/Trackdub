using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Handlers;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// Defines the <c>check</c> CLI command that reports real pipeline readiness.
/// </summary>
internal static class CheckCommand
{
    /// <summary>
    /// Creates the <c>check</c> command with the execution handler.
    /// </summary>
    public static Command Create()
    {
        var projectOption = new Option<string?>("--project")
        {
            Description = "Optional .trackdub project directory for resume/satisfied readiness detection",
        };

        var command = new Command("check", """
            Run pre-flight validation and report model/runtime readiness.

            Examples:
              trackdub check
              trackdub check --project ./my-project.trackdub
            """)
        {
            projectOption,
        };

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            cancellationToken.ThrowIfCancellationRequested();

            string? projectPath = parseResult.GetValue(projectOption);

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await CheckHandler.ExecuteAsync(
                    factory,
                    projectPath,
                    Console.Out,
                    cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }
}
