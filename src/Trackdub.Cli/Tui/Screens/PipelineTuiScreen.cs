using Spectre.Console;

using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class PipelineTuiScreen : ITuiScreen
{
    public TuiScreenId Id => TuiScreenId.Pipeline;

    public string Title => "Pipeline";

    public async Task RenderAsync(TrackdubTuiContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            context.Console.MarkupLine(
                "[yellow]No project open.[/] Press [white]o[/] to open a .trackdub project directory.");
            return;
        }

        PipelineHandler.PipelineSnapshot? snapshot = await PipelineHandler
            .TryLoadSnapshotAsync(context.Factory, context.ProjectPath, context.CancellationToken)
            .ConfigureAwait(false);

        if (snapshot is null)
        {
            context.Console.MarkupLine(
                $"[red]Could not open project:[/] {EscapeMarkup(context.ProjectPath)}");
            context.Console.MarkupLine("Press [white]o[/] to choose another project path.");
            return;
        }

        context.Console.MarkupLine(
            $"[grey]Project:[/] {EscapeMarkup(snapshot.ProjectName)}  " +
            $"[grey]Target:[/] {EscapeMarkup(snapshot.TargetLanguage ?? "(unset)")}");
        context.Console.MarkupLine(
            $"[grey]Path:[/] {EscapeMarkup(snapshot.ProjectPath)}");
        context.Console.MarkupLine(
            snapshot.IsRunReady
                ? "[green]Pipeline readiness: run-ready[/]"
                : "[yellow]Pipeline readiness: blocked (see readiness column)[/]");

        if (!string.IsNullOrWhiteSpace(context.StatusMessage))
        {
            context.Console.MarkupLine($"[cyan]{EscapeMarkup(context.StatusMessage)}[/]");
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Stage")
            .AddColumn("Last run")
            .AddColumn("Readiness")
            .AddColumn("Detail");

        foreach (PipelineHandler.PipelineStageRow row in snapshot.Stages)
        {
            table.AddRow(
                EscapeMarkup(row.DisplayName),
                FormatLastRun(row.LastRunStatus, row.LastRunAtUtc, row.FailureReason),
                FormatReadiness(row.ReadinessState, row.ReadinessReady),
                EscapeMarkup(row.ReadinessDetail ?? row.FailureReason ?? "-"));
        }

        context.Console.Write(table);
        context.Console.MarkupLine(
            "[grey]Pipeline actions:[/] [white]o[/] open  [white]s[/] run stage  [white]g[/] run all");
    }

    public async Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context)
    {
        return key.Key switch
        {
            ConsoleKey.O => await OpenProjectAsync(context).ConfigureAwait(false),
            ConsoleKey.S => await RunSelectedStageAsync(context).ConfigureAwait(false),
            ConsoleKey.G => await RunFullPipelineAsync(context).ConfigureAwait(false),
            _ => false,
        };
    }

    private static async Task<bool> OpenProjectAsync(TrackdubTuiContext context)
    {
        string path = context.Console.Prompt(
            new TextPrompt<string>("Project directory:")
                .DefaultValue(context.ProjectPath ?? string.Empty)
                .AllowEmpty());

        if (string.IsNullOrWhiteSpace(path))
        {
            context.SetStatus("Project path is required.");
            return true;
        }

        ProjectHandler.ProjectDetailSnapshot? detail = await ProjectHandler.TryLoadDetailAsync(
            context.Factory,
            path,
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

    private static async Task<bool> RunSelectedStageAsync(TrackdubTuiContext context)
    {
        if (!EnsureProjectOpen(context))
        {
            return true;
        }

        string stageName = context.Console.Prompt(
            new SelectionPrompt<string>()
                .Title("Run which stage?")
                .PageSize(10)
                .AddChoices(PipelineHandler.UiStages.Select(stage => stage.StageName))
                .UseConverter(name =>
                    PipelineHandler.UiStages.First(stage => stage.StageName == name).DisplayName));

        int exitCode = await PipelineHandler
            .RunStageAsync(context.Factory, context.ProjectPath!, stageName, context.CancellationToken)
            .ConfigureAwait(false);

        context.SetStatus(exitCode == Program.ExitSuccess
            ? $"Stage {stageName} finished successfully."
            : $"Stage {stageName} failed (exit {exitCode}).");
        return true;
    }

    private static async Task<bool> RunFullPipelineAsync(TrackdubTuiContext context)
    {
        if (!EnsureProjectOpen(context))
        {
            return true;
        }

        if (!context.Console.Confirm("Run all pipeline stages for the open project?"))
        {
            return true;
        }

        int exitCode = await PipelineHandler
            .RunFullPipelineAsync(context.Factory, context.ProjectPath!, context.CancellationToken)
            .ConfigureAwait(false);

        context.SetStatus(exitCode == Program.ExitSuccess
            ? "Pipeline run finished successfully."
            : $"Pipeline run failed (exit {exitCode}).");
        return true;
    }

    private static bool EnsureProjectOpen(TrackdubTuiContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            return true;
        }

        context.SetStatus("Open a project first (press o).");
        return false;
    }

    private static string FormatLastRun(
        StageRunStatus? status,
        DateTimeOffset? atUtc,
        string? failureReason)
    {
        if (status is null)
        {
            return "[grey]not run[/]";
        }

        string when = atUtc?.ToString("u") ?? "-";
        return status switch
        {
            StageRunStatus.Completed => $"[green]{status}[/] {when}",
            StageRunStatus.Running => $"[cyan]{status}[/] {when}",
            StageRunStatus.Failed or StageRunStatus.Canceled =>
                $"[red]{status}[/] {EscapeMarkup(failureReason ?? when)}",
            StageRunStatus.Skipped or StageRunStatus.PartiallyCompleted =>
                $"[yellow]{status}[/] {EscapeMarkup(failureReason ?? when)}",
            _ => $"{status} {when}",
        };
    }

    private static string FormatReadiness(ReadinessState? state, bool ready) =>
        state switch
        {
            null => ready ? "[green]ready[/]" : "[yellow]unknown[/]",
            _ when ready => $"[green]{state}[/]",
            _ => $"[yellow]{state}[/]",
        };

    private static string EscapeMarkup(string value) =>
        value.Replace("[", "[[", StringComparison.Ordinal);
}
