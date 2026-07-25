using Spectre.Console;

using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui;
using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class HomeTuiScreen : ITuiScreen
{
    public TuiScreenId Id => TuiScreenId.Home;

    public string Title => "Home";

    public async Task RenderAsync(TrackdubTuiContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<RecentProjectEntry> recentProjects = await TuiProjectHelper
            .LoadRecentProjectsAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            context.Console.MarkupLine(
                $"[grey]Open project:[/] [white]{TuiMarkup.Escape(context.ProjectPath)}[/]");
        }
        else
        {
            context.Console.MarkupLine("[yellow]No project open.[/] Use [white]o[/] or [white]n[/] below.");
        }

        if (!string.IsNullOrWhiteSpace(context.StatusMessage))
        {
            context.Console.MarkupLine($"[cyan]{TuiMarkup.Escape(context.StatusMessage)}[/]");
        }

        if (recentProjects.Count == 0)
        {
            context.Console.MarkupLine("[grey]No recent projects in settings.json yet.[/]");
            return;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("#")
            .AddColumn("Name")
            .AddColumn("Path")
            .AddColumn("Last opened");

        for (int index = 0; index < recentProjects.Count; index++)
        {
            RecentProjectEntry entry = recentProjects[index];
            string marker = string.Equals(entry.ProjectPath, context.ProjectPath, StringComparison.OrdinalIgnoreCase)
                ? "[green]*[/]"
                : " ";
            table.AddRow(
                $"{index + 1}",
                $"{marker} {TuiMarkup.Escape(entry.ProjectName)}",
                TuiMarkup.Escape(entry.ProjectPath),
                entry.LastOpenedAtUtc.ToString("u"));
        }

        context.Console.Write(table);
        context.Console.MarkupLine("[grey]Home actions:[/] [white]o[/] open  [white]n[/] new project");
    }

    public Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context) =>
        key.Key switch
        {
            ConsoleKey.O => OpenProjectAsync(context),
            ConsoleKey.N => CreateProjectAsync(context),
            _ => Task.FromResult(false),
        };

    private static async Task<bool> OpenProjectAsync(TrackdubTuiContext context)
    {
        IReadOnlyList<RecentProjectEntry> recentProjects = await TuiProjectHelper
            .LoadRecentProjectsAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        var choices = new List<string> { "__path__" };
        choices.AddRange(recentProjects.Select(entry => entry.ProjectPath));

        string selected = context.Console.Prompt(
            new SelectionPrompt<string>()
                .Title("Open project")
                .PageSize(12)
                .AddChoices(choices)
                .UseConverter(value => value switch
                {
                    "__path__" => "Enter path manually…",
                    _ => recentProjects.First(entry => entry.ProjectPath == value).ProjectName
                        + " — "
                        + value,
                }));

        string projectPath = selected == "__path__"
            ? context.Console.Prompt(
                new TextPrompt<string>("Project directory:")
                    .DefaultValue(context.ProjectPath ?? string.Empty)
                    .AllowEmpty())
            : selected;

        if (string.IsNullOrWhiteSpace(projectPath))
        {
            context.SetStatus("Project path is required.");
            return true;
        }

        ProjectHandler.ProjectDetailSnapshot? detail = await ProjectHandler.TryLoadDetailAsync(
            context.Factory,
            projectPath,
            context.CancellationToken).ConfigureAwait(false);

        if (detail is null)
        {
            context.SetStatus("Could not open that project path.");
            return true;
        }

        await TuiProjectHelper
            .SetOpenProjectAsync(context, detail.ProjectPath!, detail.ProjectName!, context.CancellationToken)
            .ConfigureAwait(false);
        context.SetStatus($"Opened {detail.ProjectName}.");
        return true;
    }

    private static async Task<bool> CreateProjectAsync(TrackdubTuiContext context)
    {
        string mediaPath = context.Console.Prompt(
            new TextPrompt<string>("Source media file:")
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(mediaPath))
        {
            context.SetStatus("Media path is required.");
            return true;
        }

        string? outputDirectory = context.Console.Prompt(
            new TextPrompt<string>("Output project directory (optional):")
                .AllowEmpty());

        (int exitCode, ProjectHandler.ProjectCreateResult? result) = await ProjectHandler.TryCreateAsync(
            context.Factory,
            mediaPath,
            projectName: null,
            string.IsNullOrWhiteSpace(outputDirectory) ? null : outputDirectory,
            context.CancellationToken).ConfigureAwait(false);

        if (result is null || exitCode != Program.ExitSuccess)
        {
            context.SetStatus("Project create failed.");
            return true;
        }

        await TuiProjectHelper
            .SetOpenProjectAsync(context, result.ProjectPath!, result.ProjectName!, context.CancellationToken)
            .ConfigureAwait(false);
        context.SetStatus($"Created and opened {result.ProjectName} at {result.ProjectPath}.");
        return true;
    }
}
