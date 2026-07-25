namespace Trackdub.Cli.Tui;

internal interface ITuiScreen
{
    TuiScreenId Id { get; }

    string Title { get; }

    Task RenderAsync(TrackdubTuiContext context);

    Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context);
}
