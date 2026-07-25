using Spectre.Console;

using Trackdub.Contracts;
using Trackdub.Cli.Tui;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class LogTuiScreen : ITuiScreen
{
    public TuiScreenId Id => TuiScreenId.Log;

    public string Title => "Log";

    public Task RenderAsync(TrackdubTuiContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        IAppStoragePaths storagePaths = context.Factory.GetRequiredService<IAppStoragePaths>();
        string logPath = storagePaths.LogFilePath;
        IReadOnlyList<string> lines = TuiLogTail.ReadLastLines(logPath);

        context.Console.MarkupLine($"[grey]Log file:[/] {TuiMarkup.Escape(logPath)}");
        context.Console.MarkupLine(
            $"[grey]Showing last {TuiLogTail.DefaultLineCount} lines. Press [white]r[/] to refresh.[/]");

        string body = string.Join(Environment.NewLine, lines.Select(TuiMarkup.Escape));
        context.Console.Write(
            new Panel(new Markup(body))
                .Header("trackdub.log", Justify.Left)
                .BorderColor(Color.Grey));

        return Task.CompletedTask;
    }

    public Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context) =>
        Task.FromResult(false);
}
