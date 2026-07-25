using Spectre.Console;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class PlaceholderTuiScreen : ITuiScreen
{
    private readonly string _description;

    public PlaceholderTuiScreen(TuiScreenId id, string title, string description)
    {
        Id = id;
        Title = title;
        _description = description;
    }

    public TuiScreenId Id { get; }

    public string Title { get; }

    public Task RenderAsync(TrackdubTuiContext context)
    {
        context.Console.Write(
            new Panel(_description)
                .Header(Title, Justify.Left)
                .BorderColor(Color.Grey)
                .Padding(1, 1, 1, 1));

        return Task.CompletedTask;
    }

    public Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context) =>
        Task.FromResult(false);
}
