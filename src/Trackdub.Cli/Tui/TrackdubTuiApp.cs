using Spectre.Console;

using Trackdub.Cli.Tui.Screens;
using Trackdub.Sdk;

namespace Trackdub.Cli.Tui;

internal static class TrackdubTuiApp
{
    internal static async Task<int> RunAsync(
        TrackdubSessionFactory factory,
        IAnsiConsole console,
        CancellationToken cancellationToken)
    {
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        ConsoleCancelEventHandler? cancelHandler = (_, eventArgs) =>
        {
            eventArgs.Cancel = true;
            linkedCts.Cancel();
        };

        Console.CancelKeyPress += cancelHandler;

        try
        {
            var context = new TrackdubTuiContext(factory, console, linkedCts.Token);
            IReadOnlyDictionary<TuiScreenId, ITuiScreen> screens = CreateScreens();
            var currentScreen = TuiScreenId.Models;
            var showHelp = false;

            while (!context.QuitRequested && !linkedCts.Token.IsCancellationRequested)
            {
                console.Clear();
                RenderHeader(console, currentScreen, screens);
                await screens[currentScreen].RenderAsync(context).ConfigureAwait(false);

                if (showHelp)
                {
                    RenderHelp(console);
                }
                else
                {
                    RenderFooter(console, currentScreen);
                }

                ConsoleKeyInfo key = Console.ReadKey(intercept: true);

                if (TryNavigate(key, ref currentScreen, ref showHelp))
                {
                    ClearScreenOverlay(screens[currentScreen]);
                    context.ClearStatus();
                    continue;
                }

                if (key.Key == ConsoleKey.Q)
                {
                    if (TryClearScreenOverlay(screens[currentScreen]))
                    {
                        context.ClearStatus();
                        continue;
                    }

                    break;
                }

                if (key.Key == ConsoleKey.R)
                {
                    context.ClearStatus();
                    continue;
                }

                if (key.KeyChar == '?')
                {
                    showHelp = !showHelp;
                    continue;
                }

                try
                {
                    _ = await screens[currentScreen]
                        .HandleKeyAsync(key, context)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (linkedCts.Token.IsCancellationRequested)
                {
                    context.SetStatus("Operation cancelled.");
                }
            }

            return linkedCts.Token.IsCancellationRequested
                ? Program.ExitCancelled
                : Program.ExitSuccess;
        }
        finally
        {
            if (cancelHandler is not null)
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
    }

    private static IReadOnlyDictionary<TuiScreenId, ITuiScreen> CreateScreens() =>
        new Dictionary<TuiScreenId, ITuiScreen>
        {
            [TuiScreenId.Home] = new HomeTuiScreen(),
            [TuiScreenId.Models] = new ModelsTuiScreen(),
            [TuiScreenId.Project] = new ProjectTuiScreen(),
            [TuiScreenId.Pipeline] = new PipelineTuiScreen(),
            [TuiScreenId.Log] = new LogTuiScreen(),
        };

    private static void RenderHeader(
        IAnsiConsole console,
        TuiScreenId currentScreen,
        IReadOnlyDictionary<TuiScreenId, ITuiScreen> screens)
    {
        string tabs = string.Join("  ", screens.Values
            .Where(screen => screen.Id != TuiScreenId.Log)
            .Select(screen =>
            {
                string label = TuiMarkup.Escape($"[{(int)screen.Id}] {screen.Title}");
                return screen.Id == currentScreen
                    ? $"[white bold]{label}[/]"
                    : $"[grey]{label}[/]";
            }));

        console.Write(
            new Panel(new Markup(tabs))
                .Header("[bold]Trackdub[/]", Justify.Left)
                .BorderColor(Color.Blue));
    }

    private static void RenderFooter(IAnsiConsole console, TuiScreenId currentScreen)
    {
        string screenHint = currentScreen switch
        {
            TuiScreenId.Home => "  [white]o[/] open  [white]n[/] new project",
            TuiScreenId.Models => "  [white]p[/] packs  [white]d[/] ad-hoc download  [white]a[/] all missing  [white]v[/] verify",
            TuiScreenId.Pipeline => "  [white]o[/] open  [white]s[/] run stage  [white]g[/] run all",
            _ => string.Empty,
        };

        console.MarkupLine(
            $"[grey]q[/] quit  [grey]r[/] refresh  [grey]?[/] help  [grey]F6[/] log{screenHint}");
    }

    private static void RenderHelp(IAnsiConsole console)
    {
        console.Write(
            new Panel(
                """
                Navigation
                  1 Home   2 Models   3 Project   4 Pipeline   F6 Log

                Global
                  q  Quit
                  r  Refresh current screen
                  ?  Toggle this help

                Models screen
                  p  Starter packs (download pack files or apply settings separately)
                  d  Ad-hoc download menu (all missing, one model, or cancel)
                  a  Download all missing commercial bundled models
                  v  Verify checksums for one model
                  Pack Installed counts are checksum-valid only, not runtime-ready.
                  Apply is blocked when required models lack commercial verification.
                  Pickers: ↑↓ move, Enter select, Esc/c cancel picker

                Home screen
                  o  Open recent project or enter path
                  n  Create project from media (ProjectHandler)

                Pipeline screen
                  o  Open a .trackdub project directory
                  s  Run one stage (user-triggered)
                  g  Run all stages for the open project

                Project screen
                  Read-only spine, artifacts, and SQLite stage runs for open project

                Log screen (F6)
                  Read-only tail of trackdub.log; r refreshes
                """)
                .Header("Help", Justify.Left)
                .BorderColor(Color.Grey));
    }

    private static void ClearScreenOverlay(ITuiScreen screen)
    {
        if (screen is ITuiOverlayScreen overlay)
        {
            overlay.ClearOverlay();
        }
    }

    private static bool TryClearScreenOverlay(ITuiScreen screen)
    {
        if (screen is ITuiOverlayScreen { HasOverlay: true } overlay)
        {
            overlay.ClearOverlay();
            return true;
        }

        return false;
    }

    private static bool TryNavigate(
        ConsoleKeyInfo key,
        ref TuiScreenId currentScreen,
        ref bool showHelp)
    {
        showHelp = false;

        if (key.Key == ConsoleKey.D1 || key.KeyChar == '1')
        {
            currentScreen = TuiScreenId.Home;
            return true;
        }

        if (key.Key == ConsoleKey.D2 || key.KeyChar == '2')
        {
            currentScreen = TuiScreenId.Models;
            return true;
        }

        if (key.Key == ConsoleKey.D3 || key.KeyChar == '3')
        {
            currentScreen = TuiScreenId.Project;
            return true;
        }

        if (key.Key == ConsoleKey.D4 || key.KeyChar == '4')
        {
            currentScreen = TuiScreenId.Pipeline;
            return true;
        }

        if (key.Key == ConsoleKey.F6)
        {
            currentScreen = TuiScreenId.Log;
            return true;
        }

        return false;
    }
}
