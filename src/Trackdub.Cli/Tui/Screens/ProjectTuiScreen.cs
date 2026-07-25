using Spectre.Console;

using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class ProjectTuiScreen : ITuiScreen
{
    public TuiScreenId Id => TuiScreenId.Project;

    public string Title => "Project";

    public async Task RenderAsync(TrackdubTuiContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            context.Console.MarkupLine(
                "[yellow]No project open.[/] Press [white]o[/] on Home or Pipeline to open one.");
            return;
        }

        ProjectHandler.ProjectDetailSnapshot? detail = await ProjectHandler.TryLoadDetailAsync(
            context.Factory,
            context.ProjectPath,
            context.CancellationToken).ConfigureAwait(false);

        if (detail is null)
        {
            context.Console.MarkupLine(
                $"[red]Could not load project:[/] {TuiMarkup.Escape(context.ProjectPath)}");
            return;
        }

        context.Console.MarkupLine(
            $"[grey]Name:[/] {TuiMarkup.Escape(detail.ProjectName ?? "-")}  " +
            $"[grey]Target:[/] {TuiMarkup.Escape(detail.TargetLanguage ?? "(unset)")}");
        context.Console.MarkupLine(
            $"[grey]Source media:[/] {TuiMarkup.Escape(detail.SourceMediaPath ?? "-")}");
        context.Console.MarkupLine(
            $"[grey]Path:[/] {TuiMarkup.Escape(detail.ProjectPath ?? "-")}");

        if (!string.IsNullOrWhiteSpace(context.StatusMessage))
        {
            context.Console.MarkupLine($"[cyan]{TuiMarkup.Escape(context.StatusMessage)}[/]");
        }

        var artifactTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("Artifacts")
            .AddColumn("Kind")
            .AddColumn("Relative path")
            .AddColumn("Present");

        IReadOnlyList<ProjectHandler.ProjectArtifactSnapshot> artifacts = detail.Artifacts ?? [];
        if (artifacts.Count == 0)
        {
            artifactTable.AddRow("-", "-", "[grey]none yet[/]");
        }
        else
        {
            foreach (ProjectHandler.ProjectArtifactSnapshot artifact in artifacts)
            {
                artifactTable.AddRow(
                    TuiMarkup.Escape(artifact.Kind ?? "-"),
                    TuiMarkup.Escape(artifact.RelativePath ?? "-"),
                    artifact.Exists ? "[green]yes[/]" : "[yellow]no[/]");
            }
        }

        context.Console.Write(artifactTable);

        var runTable = new Table()
            .Border(TableBorder.Rounded)
            .Title("Stage runs (SQLite, newest first)")
            .AddColumn("Stage")
            .AddColumn("Status")
            .AddColumn("Started")
            .AddColumn("Detail");

        IReadOnlyList<ProjectHandler.ProjectStageRunSnapshot> runs = detail.StageRuns ?? [];
        if (runs.Count == 0)
        {
            runTable.AddRow("-", "[grey]not run[/]", "-", "-");
        }
        else
        {
            foreach (ProjectHandler.ProjectStageRunSnapshot run in runs.Take(12))
            {
                runTable.AddRow(
                    TuiMarkup.Escape(run.Stage ?? "-"),
                    FormatRunStatus(run.Status),
                    run.StartedAtUtc.ToString("u"),
                    TuiMarkup.Escape(run.ReasonCode ?? "-"));
            }
        }

        context.Console.Write(runTable);
    }

    public Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context) =>
        Task.FromResult(false);

    private static string FormatRunStatus(string? status) =>
        status switch
        {
            "Completed" => "[green]Completed[/]",
            "Running" => "[cyan]Running[/]",
            "Failed" or "Canceled" => $"[red]{status}[/]",
            "Skipped" or "PartiallyCompleted" => $"[yellow]{status}[/]",
            _ => status ?? "-",
        };
}
