using System.CommandLine;
using System.CommandLine.Completions;
using System.CommandLine.Parsing;

using Trackdub.Sdk;

namespace Trackdub.Cli.Commands;

/// <summary>
/// Emits newline-delimited completion candidates for shell integration scripts.
/// </summary>
internal static class CompleteCommand
{
    public static Command Create(RootCommand parseRoot)
    {
        var positionOption = new Option<int>("--position")
        {
            Description = "Zero-based cursor position in --line",
            Required = true,
        };

        var lineOption = new Option<string>("--line")
        {
            Description = "Command line text being completed",
            Required = true,
        };

        var command = new Command("complete", "Emit shell completion candidates for a partial command line")
        {
            positionOption,
            lineOption,
        };

        command.SetAction((ParseResult parseResult, CancellationToken _) =>
        {
            int cursorPosition = parseResult.GetValue(positionOption);
            string commandLine = parseResult.GetValue(lineOption)!;

            (string normalizedLine, int normalizedPosition) = CliCompletionLineNormalizer.Normalize(
                commandLine,
                cursorPosition,
                RootCommand.ExecutableName);

            ParseResult targetParse = parseRoot.Parse(normalizedLine);
            IEnumerable<CompletionItem> completions = targetParse.GetCompletions(normalizedPosition);

            foreach (CompletionItem completion in completions)
            {
                string? label = string.IsNullOrWhiteSpace(completion.Label)
                    ? completion.InsertText
                    : completion.Label;
                if (!string.IsNullOrWhiteSpace(label))
                {
                    Console.Out.WriteLine(label);
                }
            }

            return Task.FromResult(Program.ExitSuccess);
        });

        return command;
    }
}
