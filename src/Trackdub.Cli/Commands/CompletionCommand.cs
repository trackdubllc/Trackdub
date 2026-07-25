using System.CommandLine;
using System.CommandLine.Parsing;

using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// Generates shell completion scripts for bash, zsh, and PowerShell.
/// </summary>
internal static class CompletionCommand
{
    private static readonly string[] SupportedShells = ["bash", "zsh", "pwsh"];

    public static Command Create()
    {
        var shellArgument = new Argument<string>("shell")
        {
            Description = "Target shell (bash, zsh, or pwsh)",
        };
        shellArgument.AcceptOnlyFromAmong(SupportedShells);

        var executableNameOption = new Option<string>("--executable-name")
        {
            Description = "Command name to register in the shell script (defaults to the current executable name)",
            DefaultValueFactory = _ => RootCommand.ExecutableName,
        };

        var command = new Command("completion", """
            Print a shell completion script for bash, zsh, or pwsh.

            Examples:
              eval "$(trackdub completion bash)"
              trackdub completion zsh >> ~/.zshrc
              trackdub completion pwsh | Out-String | Invoke-Expression
            """)
        {
            shellArgument,
            executableNameOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken _) =>
        {
            string shell = parseResult.GetValue(shellArgument)!;
            string executableName = parseResult.GetValue(executableNameOption)!;

            try
            {
                string script = CliCompletionScripts.Generate(shell, executableName);
                Console.Out.Write(script);
                if (!script.EndsWith('\n'))
                {
                    Console.Out.WriteLine();
                }
            }
            catch (ArgumentOutOfRangeException ex)
            {
                CliErrorReporter.ReportValidationError(
                    ErrorCode.InvalidArgument,
                    ex.Message,
                    "shell");
                return Task.FromResult(Program.ExitArgumentError);
            }

            return Task.FromResult(Program.ExitSuccess);
        });

        return command;
    }
}
