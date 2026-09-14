using Spectre.Console;

using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class PipelineTuiScreen : ITuiScreen, ITuiOverlayScreen
{
    private const string BackChoice = "__back__";
    private const string RunChoice = "__run__";
    private const string ConfigureChoice = "__configure__";
    private const string DefaultsChoice = "__run_defaults__";
    private const string YesChoice = "__yes__";
    private const string NoChoice = "__no__";
    private const string VideoChoice = "__video__";
    private const string EnterAliasChoice = "__enter_alias__";

    public TuiScreenId Id => TuiScreenId.Pipeline;

    public string Title => "Pipeline";

    public bool HasOverlay => _picker is not null;

    public void ClearOverlay()
    {
        _picker = null;
        _pickerHandler = null;
    }

    private TuiInlinePicker? _picker;
    private Func<string, Task<bool>>? _pickerHandler;

    // Full-pipeline wizard accumulation state (reset at start of each wizard flow)
    private PipelineWizardState _wizard = new();

    // Compile-checked accessor for the current wizard state (visible to Trackdub.Sdk.Tests
    // via InternalsVisibleTo) so tests do not reach into the private field via reflection.
    internal PipelineWizardState WizardState => _wizard;

    // Stage-run wizard state (independent of the full-pipeline wizard)
    private string? _wStageName;
    private string? _wModelAlias;

    // Snapshot cached for use by wizard handlers that need target-language state
    private PipelineHandler.PipelineSnapshot? _cachedSnapshot;

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

        _cachedSnapshot = snapshot;

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
            "[grey]Pipeline actions:[/] [white]o[/] open  [white]s[/] run stage  [white]g[/] run all (configurable)");

        _picker?.Render(context.Console);
    }

    public async Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context)
    {
        if (_picker is not null)
        {
            return await TryHandlePickerKeyAsync(key, context).ConfigureAwait(false);
        }

        return key.Key switch
        {
            ConsoleKey.O => await OpenProjectAsync(context).ConfigureAwait(false),
            ConsoleKey.S => await BeginStagePickerAsync(context).ConfigureAwait(false),
            ConsoleKey.G => await BeginRunAllMenuAsync(context).ConfigureAwait(false),
            _ => false,
        };
    }

    private async Task<bool> TryHandlePickerKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context)
    {
        if (_picker is null)
        {
            return false;
        }

        switch (key.Key)
        {
            case ConsoleKey.UpArrow:
                _picker.MoveUp();
                return true;
            case ConsoleKey.DownArrow:
                _picker.MoveDown();
                return true;
            case ConsoleKey.Escape:
                ClearOverlay();
                return true;
            case ConsoleKey.C:
                ClearOverlay();
                return true;
            case ConsoleKey.Enter:
                {
                    TuiInlinePicker picker = _picker;
                    Func<string, Task<bool>>? handler = _pickerHandler;
                    ClearOverlay();
                    if (handler is not null)
                    {
                        return await handler(picker.SelectedValue).ConfigureAwait(false);
                    }

                    return true;
                }

            default:
                return true;
        }
    }

    // -------------------------------------------------------------------------
    // Open project
    // -------------------------------------------------------------------------

    private static async Task<bool> OpenProjectAsync(TrackdubTuiContext context)
    {
        string path = TuiPathPrompt.Ask(
            context.Console,
            "Project directory:",
            context.ProjectPath ?? string.Empty);

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

    // -------------------------------------------------------------------------
    // Stage run wizard: stage picker → model alias picker → fire
    // -------------------------------------------------------------------------

    private Task<bool> BeginStagePickerAsync(TrackdubTuiContext context)
    {
        if (!EnsureProjectOpen(context))
        {
            return Task.FromResult(true);
        }

        var choices = new List<(string Value, string Label)> { (BackChoice, "Cancel") };
        choices.AddRange(PipelineHandler.UiStages.Select(s => (s.StageName, s.DisplayName)));

        _picker = new TuiInlinePicker("Run which stage?", choices);
        _pickerHandler = choice => HandleStageChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private Task<bool> HandleStageChoiceAsync(TrackdubTuiContext context, string stageName)
    {
        if (stageName == BackChoice)
        {
            return Task.FromResult(true);
        }

        _wStageName = stageName;
        _wModelAlias = null;
        return BeginStageModelPickerAsync(context);
    }

    private Task<bool> BeginStageModelPickerAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Model alias override?",
            [
                (BackChoice,       "Back"),
                ("__default__",    "Default model"),
                (EnterAliasChoice, "Enter alias…"),
            ]);
        _pickerHandler = choice => HandleStageModelChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleStageModelChoiceAsync(TrackdubTuiContext context, string choice)
    {
        switch (choice)
        {
            case BackChoice:
                return await BeginStagePickerAsync(context).ConfigureAwait(false);

            case "__default__":
                _wModelAlias = null;
                break;

            case EnterAliasChoice:
                string alias = context.Console.Prompt(
                    new TextPrompt<string>("Model alias (e.g. whisper-small):")
                        .AllowEmpty());
                _wModelAlias = string.IsNullOrWhiteSpace(alias) ? null : alias;
                break;
        }

        return await FireStageRunAsync(context).ConfigureAwait(false);
    }

    private async Task<bool> FireStageRunAsync(TrackdubTuiContext context)
    {
        string stageName = _wStageName!;
        string? modelAlias = _wModelAlias;

        int exitCode = await PipelineHandler
            .RunStageAsync(context.Factory, context.ProjectPath!, stageName, modelAlias, context.CancellationToken)
            .ConfigureAwait(false);

        context.SetStatus(exitCode == Program.ExitSuccess
            ? $"Stage {stageName} finished successfully."
            : $"Stage {stageName} failed (exit {exitCode}).");
        return true;
    }

    // -------------------------------------------------------------------------
    // Full pipeline wizard: top menu → voice clone → export format →
    //   subtitle format → subtitle source → advanced options → fire
    // -------------------------------------------------------------------------

    private Task<bool> BeginRunAllMenuAsync(TrackdubTuiContext context)
    {
        if (!EnsureProjectOpen(context))
        {
            return Task.FromResult(true);
        }

        _picker = new TuiInlinePicker(
            "Run all — pipeline options",
            [
                (DefaultsChoice,  "Run now — all defaults"),
                (ConfigureChoice, "Configure options first…"),
                (BackChoice,      "Cancel"),
            ]);
        _pickerHandler = choice => HandleRunAllMenuChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleRunAllMenuChoiceAsync(TrackdubTuiContext context, string choice)
    {
        switch (choice)
        {
            case BackChoice:
                return true;

            case DefaultsChoice:
                return await FireFullPipelineAsync(context, new PipelineHandler.TuiPipelineRunOptions())
                    .ConfigureAwait(false);

            case ConfigureChoice:
                ResetWizardState();

                // If target language is unset, prompt before the wizard starts
                if (string.IsNullOrWhiteSpace(_cachedSnapshot?.TargetLanguage))
                {
                    string lang = context.Console.Prompt(
                        new TextPrompt<string>("Target language BCP-47 (e.g. es, fr, de):")
                            .AllowEmpty());
                    _wizard.TargetLanguageOverride = string.IsNullOrWhiteSpace(lang) ? null : lang;
                    if (_wizard.TargetLanguageOverride is null)
                    {
                        context.SetStatus("Target language is required to run the pipeline.");
                        return true;
                    }
                }

                return await BeginVoiceClonePickerAsync(context).ConfigureAwait(false);

            default:
                return true;
        }
    }

    private Task<bool> BeginVoiceClonePickerAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Clone speaker voices from source audio?",
            [
                (NoChoice,   "No — use stock voice packs (default)"),
                (YesChoice,  "Yes — clone speaker voices"),
                (BackChoice, "Back"),
            ]);
        _pickerHandler = choice => HandleVoiceCloneChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleVoiceCloneChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return await BeginRunAllMenuAsync(context).ConfigureAwait(false);
        }

        _wizard.VoiceClone = choice == YesChoice;
        return await BeginExportFormatPickerAsync(context).ConfigureAwait(false);
    }

    private Task<bool> BeginExportFormatPickerAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Export container format",
            [
                ("__auto__", "Pipeline default (MP4)"),
                ("mp4",      "MP4"),
                ("mkv",      "MKV"),
                (BackChoice, "Back"),
            ]);
        _pickerHandler = choice => HandleExportFormatChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleExportFormatChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return await BeginVoiceClonePickerAsync(context).ConfigureAwait(false);
        }

        _wizard.ExportFormat = choice == "__auto__" ? null : choice;
        return await BeginSubtitleFormatPickerAsync(context).ConfigureAwait(false);
    }

    private Task<bool> BeginSubtitleFormatPickerAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Subtitle format",
            [
                ("srt",      "SRT (default)"),
                ("vtt",      "VTT"),
                ("ass",      "ASS"),
                ("__none__", "None — no subtitle file"),
                ("__skip__", "Pipeline default (SRT)"),
                (BackChoice, "Back"),
            ]);
        _pickerHandler = choice => HandleSubtitleFormatChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleSubtitleFormatChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return await BeginExportFormatPickerAsync(context).ConfigureAwait(false);
        }

        _wizard.SubtitleFormats = choice switch
        {
            "__none__" => [],
            "__skip__" => null,
            _ => [choice],
        };

        return await BeginSubtitleSourcePickerAsync(context).ConfigureAwait(false);
    }

    private Task<bool> BeginSubtitleSourcePickerAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Subtitle transcript source",
            [
                ("translated", "Translated (default)"),
                ("transcript", "Original transcript"),
                ("bilingual",  "Bilingual (both)"),
                (BackChoice,   "Back"),
            ]);
        _pickerHandler = choice => HandleSubtitleSourceChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleSubtitleSourceChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return await BeginSubtitleFormatPickerAsync(context).ConfigureAwait(false);
        }

        _wizard.SubtitleSource = choice == "translated" ? null : choice;
        return await BeginAdvancedPickerAsync(context).ConfigureAwait(false);
    }

    private Task<bool> BeginAdvancedPickerAsync(TrackdubTuiContext context)
    {
        static string Toggle(bool on) => on ? "on" : "off";

        _picker = new TuiInlinePicker(
            "Advanced options",
            [
                (RunChoice,        "Run now — use selected settings"),
                ("__timbre__",     $"Timbre polish: {Toggle(_wizard.TimbrePolish)}"),
                ("__pan__",        $"Restore pan: {Toggle(_wizard.RestorePan)}"),
                ("__loudness__",   $"Match loudness: {Toggle(_wizard.MatchLoudness)}"),
                ("__asr__",        $"ASR text refinement: {Toggle(_wizard.AsrRefinement)}"),
                ("__burnin__",     $"Burn-in subtitles: {Toggle(_wizard.BurnIn)}"),
                ("__forcererun__", $"Force rerun: {Toggle(_wizard.ForceRerun)}"),
                (VideoChoice,      $"Video encoder: {_wizard.VideoEncoder ?? "auto"}"),
                (BackChoice,       "Back"),
            ]);
        _pickerHandler = choice => HandleAdvancedChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleAdvancedChoiceAsync(TrackdubTuiContext context, string choice)
    {
        switch (choice)
        {
            case BackChoice:
                return await BeginSubtitleSourcePickerAsync(context).ConfigureAwait(false);

            case RunChoice:
                return await FireFullPipelineAsync(context, _wizard.ToRunOptions())
                    .ConfigureAwait(false);

            case VideoChoice:
                return await BeginVideoEncoderPickerAsync(context).ConfigureAwait(false);

            // Toggles — flip state and re-open advanced picker
            case "__timbre__": _wizard.TimbrePolish = !_wizard.TimbrePolish; break;
            case "__pan__": _wizard.RestorePan = !_wizard.RestorePan; break;
            case "__loudness__": _wizard.MatchLoudness = !_wizard.MatchLoudness; break;
            case "__asr__": _wizard.AsrRefinement = !_wizard.AsrRefinement; break;
            case "__burnin__": _wizard.BurnIn = !_wizard.BurnIn; break;
            case "__forcererun__": _wizard.ForceRerun = !_wizard.ForceRerun; break;
        }

        return await BeginAdvancedPickerAsync(context).ConfigureAwait(false);
    }

    private Task<bool> BeginVideoEncoderPickerAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Video encoder",
            [
                ("auto",         "Auto (default)"),
                ("nvenc",        "NVENC (NVIDIA)"),
                ("qsv",          "QSV (Intel)"),
                ("amf",          "AMF (AMD)"),
                ("software",     "Software (CPU)"),
                ("videotoolbox", "VideoToolbox (Apple)"),
                ("vaapi",        "VAAPI (Linux GPU)"),
                (BackChoice,     "Back"),
            ]);
        _pickerHandler = choice => HandleVideoEncoderChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleVideoEncoderChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return await BeginAdvancedPickerAsync(context).ConfigureAwait(false);
        }

        _wizard.VideoEncoder = choice == "auto" ? null : choice;
        return await BeginAdvancedPickerAsync(context).ConfigureAwait(false);
    }

    private async Task<bool> FireFullPipelineAsync(
        TrackdubTuiContext context,
        PipelineHandler.TuiPipelineRunOptions options)
    {
        int exitCode = await PipelineHandler
            .RunFullPipelineAsync(context.Factory, context.ProjectPath!, options, context.CancellationToken)
            .ConfigureAwait(false);

        context.SetStatus(exitCode == Program.ExitSuccess
            ? "Pipeline run finished successfully."
            : $"Pipeline run failed (exit {exitCode}).");
        return true;
    }

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static bool EnsureProjectOpen(TrackdubTuiContext context)
    {
        if (!string.IsNullOrWhiteSpace(context.ProjectPath))
        {
            return true;
        }

        context.SetStatus("Open a project first (press o).");
        return false;
    }

    private void ResetWizardState() => _wizard = new PipelineWizardState();

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
