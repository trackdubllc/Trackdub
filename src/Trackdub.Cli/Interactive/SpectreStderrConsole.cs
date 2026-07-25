using Spectre.Console;

namespace Trackdub.Cli.Interactive;

/// <summary>
/// Shared Spectre console that writes interactive UI to stderr so stdout stays machine-readable.
/// </summary>
internal static class SpectreStderrConsole
{
    internal static IAnsiConsole Create() =>
        AnsiConsole.Create(
            new AnsiConsoleSettings
            {
                Ansi = AnsiSupport.Detect,
                ColorSystem = ColorSystemSupport.Detect,
                Interactive = InteractionSupport.Yes,
                Out = new AnsiConsoleOutput(Console.Error),
            });
}
