using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Cli.Interactive;
using Trackdub.Cli.Tui;
using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

internal static class UiCommand
{
    public static Command Create(Func<bool>? isInteractive = null)
    {
        var command = new Command("ui", """
            Full-screen terminal UI for model management and pipeline work.

            Examples:
              trackdub ui
              trackdub ui --model-directory ./models
              trackdub
            """);

        command.SetAction(async (ParseResult parseResult, CancellationToken cancellationToken) =>
        {
            if (!(isInteractive?.Invoke() ?? IsInteractiveTerminal()))
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    "The TUI requires an interactive terminal (stdin and stderr must not be redirected).",
                    "ui");
                return Program.ExitArgumentError;
            }

            TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(parseResult, out int buildExitCode);
            if (factory is null)
            {
                return buildExitCode;
            }

            using (factory)
            {
                return await RunAsync(factory, cancellationToken).ConfigureAwait(false);
            }
        });

        return command;
    }

    internal static bool IsInteractiveTerminal() =>
        !Console.IsInputRedirected && !Console.IsErrorRedirected;

    internal static async Task<int> RunFromMainAsync(
        string? modelDirectory,
        CancellationToken cancellationToken)
    {
        if (!IsInteractiveTerminal())
        {
            return Program.ExitArgumentError;
        }

        TrackdubSessionFactory? factory = CliParseHelpers.TryBuildFactory(modelDirectory, out int buildExitCode);
        if (factory is null)
        {
            return buildExitCode;
        }

        using (factory)
        {
            return await RunAsync(factory, cancellationToken).ConfigureAwait(false);
        }
    }

    private static Task<int> RunAsync(
        TrackdubSessionFactory factory,
        CancellationToken cancellationToken)
    {
        var console = SpectreStderrConsole.Create();
        return TrackdubTuiApp.RunAsync(factory, console, cancellationToken);
    }
}
