using Spectre.Console;

namespace Trackdub.Cli.Tui;

internal static class TuiPathPrompt
{
    internal static string Ask(IAnsiConsole console, string title, string? defaultValue = null)
    {
        ArgumentNullException.ThrowIfNull(console);

        TextPrompt<string> prompt = new TextPrompt<string>(title).AllowEmpty();
        if (defaultValue is not null)
        {
            prompt.DefaultValue(defaultValue);
        }

        return UserPathText.Normalize(console.Prompt(prompt));
    }
}
