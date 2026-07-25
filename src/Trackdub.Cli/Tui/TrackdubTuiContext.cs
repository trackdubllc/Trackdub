using Spectre.Console;

using Trackdub.Sdk;

namespace Trackdub.Cli.Tui;

internal sealed class TrackdubTuiContext(
    TrackdubSessionFactory factory,
    IAnsiConsole console,
    CancellationToken cancellationToken)
{
    internal TrackdubSessionFactory Factory { get; } = factory;

    internal IAnsiConsole Console { get; } = console;

    internal CancellationToken CancellationToken { get; } = cancellationToken;

    internal bool QuitRequested { get; private set; }

    internal string? StatusMessage { get; set; }

    internal string? ProjectPath { get; set; }

    internal void RequestQuit() => QuitRequested = true;

    internal void SetStatus(string message) => StatusMessage = message;

    internal void ClearStatus() => StatusMessage = null;
}
