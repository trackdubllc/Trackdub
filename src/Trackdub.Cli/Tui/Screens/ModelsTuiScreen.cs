using Spectre.Console;

using Trackdub.Cli.Handlers;
using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.StarterPacks;
using Trackdub.Domain;
using Trackdub.Sdk;

namespace Trackdub.Cli.Tui.Screens;

internal sealed class ModelsTuiScreen : ITuiScreen, ITuiOverlayScreen
{
    private const string BackChoice = "__back__";
    private const string AllMissingChoice = "__all__";
    private const string PickOneChoice = "__one__";
    private const string YesChoice = "__yes__";
    private const string NoChoice = "__no__";
    private const string PackDownloadChoice = "__pack_download__";
    private const string PackApplyChoice = "__pack_apply__";

    private TuiInlinePicker? _picker;
    private Func<string, Task<bool>>? _pickerHandler;
    private List<ModelInventoryEntry> _missingCandidates = [];
    private IReadOnlyList<StarterPackSummary> _packSummaries = [];

    public TuiScreenId Id => TuiScreenId.Models;

    public string Title => "Models";

    public bool HasOverlay => _picker is not null;

    public void ClearOverlay()
    {
        _picker = null;
        _pickerHandler = null;
    }

    public async Task RenderAsync(TrackdubTuiContext context)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        IReadOnlyList<ModelInventoryEntry> entries = await ModelsHandler
            .GetInventoryAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        _packSummaries = await StarterPacksHandler
            .ListSummariesAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<ModelInventoryEntry> ordered = entries
            .OrderBy(entry => entry.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();

        int readyCount = ordered.Count(entry =>
            entry.State is ModelCacheState.Ready or ModelCacheState.Installed);
        int missingCount = ordered.Count - readyCount;
        int manualInstallCount = ordered.Count(entry => !entry.CanAutoDownload);

        string summary =
            $"[grey]Bundled manifest models:[/] {ordered.Count}  " +
            $"[green]ready {readyCount}[/]  " +
            $"[yellow]needs attention {missingCount}[/]";
        if (manualInstallCount > 0)
        {
            summary += $"  [grey]manual install {manualInstallCount}[/]";
        }

        context.Console.MarkupLine(summary);

        if (_packSummaries.Count > 0)
        {
            var packTable = new Table()
                .Border(TableBorder.Rounded)
                .AddColumn("Pack")
                .AddColumn("Profile")
                .AddColumn("Required")
                .AddColumn("Installed")
                .AddColumn("Status");

            foreach (StarterPackSummary pack in _packSummaries)
            {
                string profileLabel = pack.ProfileIds.Count > 0 ? pack.ProfileIds[0] : "default";
                string installedLabel = $"{pack.InstalledCount}/{pack.RequiredCount}";
                string status = string.IsNullOrWhiteSpace(pack.StatusLabel)
                    ? pack switch
                    {
                        { CanApply: true } => "[green]ready to apply[/]",
                        { RequiresVoiceCloningConsent: true } => "[yellow]consent required[/]",
                        _ => "[grey]download first[/]",
                    }
                    : FormatPackStatus(pack.StatusLabel);

                packTable.AddRow(
                    EscapeMarkup(pack.DisplayName),
                    EscapeMarkup(profileLabel),
                    pack.RequiredCount.ToString(),
                    installedLabel,
                    status);
            }

            context.Console.Write(packTable);
            context.Console.MarkupLine(
                "[grey]Installed counts are checksum-valid files, not runtime-ready. Basic may be the only apply-ready pack until all required models pass commercial verification.[/]");
        }

        if (!string.IsNullOrWhiteSpace(context.StatusMessage))
        {
            context.Console.MarkupLine($"[cyan]{EscapeMarkup(context.StatusMessage)}[/]");
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .AddColumn("Model")
            .AddColumn("Task")
            .AddColumn("State");

        foreach (ModelInventoryEntry entry in ordered)
        {
            table.AddRow(
                EscapeMarkup(TuiMarkup.FormatModelLabel(entry)),
                EscapeMarkup(entry.Task),
                FormatState(entry.State));
        }

        context.Console.Write(table);

        if (_picker is not null)
        {
            _picker.Render(context.Console);
        }
        else
        {
            context.Console.MarkupLine(
                "[grey]Models actions:[/] [white]p[/] packs  [white]d[/] ad-hoc download  [white]a[/] all missing  [white]v[/] verify");
        }
    }

    public async Task<bool> HandleKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context)
    {
        if (_picker is not null)
        {
            if (await TryHandlePickerKeyAsync(key, context).ConfigureAwait(false))
            {
                return true;
            }

            ClearOverlay();
        }

        return key.Key switch
        {
            ConsoleKey.P => await BeginPacksMenuAsync(context).ConfigureAwait(false),
            ConsoleKey.D => await BeginDownloadMenuAsync(context).ConfigureAwait(false),
            ConsoleKey.A => await BeginDownloadAllConfirmAsync(context).ConfigureAwait(false),
            ConsoleKey.V => await BeginVerifyPickerAsync(context).ConfigureAwait(false),
            _ => false,
        };
    }

    private async Task<bool> TryHandlePickerKeyAsync(ConsoleKeyInfo key, TrackdubTuiContext context)
    {
        if (_picker is null || _pickerHandler is null)
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
            case ConsoleKey.C:
                ClearOverlay();
                return true;
            case ConsoleKey.Enter:
                {
                    TuiInlinePicker picker = _picker;
                    Func<string, Task<bool>> handler = _pickerHandler;
                    ClearOverlay();
                    return await handler(picker.SelectedValue).ConfigureAwait(false);
                }
            case ConsoleKey.Escape:
                ClearOverlay();
                return true;
        }

        return false;
    }

    private static async Task<List<ModelInventoryEntry>> GetMissingCandidatesAsync(TrackdubTuiContext context)
    {
        IReadOnlyList<ModelInventoryEntry> entries = await ModelsHandler
            .GetInventoryAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        return entries
            .Where(entry => entry.CommercialAllowed)
            .Where(entry => entry.CanAutoDownload)
            .Where(entry => entry.State is not ModelCacheState.Ready and not ModelCacheState.Installed)
            .OrderBy(entry => entry.ModelId, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private Task<bool> BeginPacksMenuAsync(TrackdubTuiContext context)
    {
        _picker = new TuiInlinePicker(
            "Starter packs",
            [
                (BackChoice, "Cancel"),
                (PackDownloadChoice, "Download pack…"),
                (PackApplyChoice, "Apply pack…"),
            ]);
        _pickerHandler = choice => HandlePacksMenuChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private Task<bool> HandlePacksMenuChoiceAsync(TrackdubTuiContext context, string choice) =>
        choice switch
        {
            BackChoice => Task.FromResult(true),
            PackDownloadChoice => BeginPackPickerAsync(context, forApply: false),
            PackApplyChoice => BeginPackPickerAsync(context, forApply: true),
            _ => Task.FromResult(true),
        };

    private async Task<bool> BeginPackPickerAsync(TrackdubTuiContext context, bool forApply)
    {
        _packSummaries = await StarterPacksHandler
            .ListSummariesAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        if (_packSummaries.Count == 0)
        {
            context.SetStatus("No starter packs are available.");
            return true;
        }

        var choices = new List<(string Value, string Label)>
        {
            (BackChoice, "Cancel"),
        };
        choices.AddRange(_packSummaries.Select(pack => (pack.Id, pack.DisplayName)));

        _picker = new TuiInlinePicker(forApply ? "Apply which pack?" : "Download which pack?", choices);
        _pickerHandler = choice => HandlePackChoiceAsync(context, choice, forApply);
        return true;
    }

    private async Task<bool> HandlePackChoiceAsync(TrackdubTuiContext context, string choice, bool forApply)
    {
        if (choice == BackChoice)
        {
            return await BeginPacksMenuAsync(context).ConfigureAwait(false);
        }

        IStarterPackCatalog catalog = context.Factory.GetRequiredService<IStarterPackCatalog>();
        StarterPackDefinition pack = await catalog.GetAsync(choice, context.CancellationToken).ConfigureAwait(false);

        if (pack.Profiles.Count <= 1)
        {
            string profileId = pack.Profiles[0].Id;
            return forApply
                ? await BeginApplyPackConfirmAsync(context, pack.Id, profileId).ConfigureAwait(false)
                : await RunPackDownloadAsync(context, pack.Id, profileId).ConfigureAwait(false);
        }

        var choices = new List<(string Value, string Label)>
        {
            (BackChoice, "Cancel"),
        };
        choices.AddRange(pack.Profiles.Select(profile => (profile.Id, profile.DisplayName)));

        _picker = new TuiInlinePicker(
            forApply ? $"Apply profile for {pack.DisplayName}" : $"Download profile for {pack.DisplayName}",
            choices);
        _pickerHandler = profileChoice => HandlePackProfileChoiceAsync(context, profileChoice, pack.Id, forApply);
        return true;
    }

    private Task<bool> HandlePackProfileChoiceAsync(
        TrackdubTuiContext context,
        string profileChoice,
        string packId,
        bool forApply)
    {
        if (profileChoice == BackChoice)
        {
            return BeginPackPickerAsync(context, forApply);
        }

        return forApply
            ? BeginApplyPackConfirmAsync(context, packId, profileChoice)
            : RunPackDownloadAsync(context, packId, profileChoice);
    }

    private async Task<bool> BeginApplyPackConfirmAsync(TrackdubTuiContext context, string packId, string profileId)
    {
        StarterPackSummary summary = await StarterPacksHandler
            .GetSummaryAsync(context.Factory, packId, profileId, context.CancellationToken)
            .ConfigureAwait(false);
        if (summary.HasCommercialVerificationGap || summary.InstalledCount < summary.RequiredCount)
        {
            context.SetStatus(summary.BlockedReason ?? "This pack cannot be applied yet.");
            return true;
        }

        bool needsConsent = NeedsVoiceCloningConsentPrompt(summary);
        if (needsConsent)
        {
            _picker = new TuiInlinePicker(
                "Premium voice cloning requires explicit consent. Apply anyway?",
                [
                    (NoChoice, "Cancel"),
                    (YesChoice, "Yes, apply with consent"),
                ]);
            _pickerHandler = choice => choice switch
            {
                YesChoice => RunPackApplyAsync(context, packId, profileId, acceptVoiceCloningConsent: true),
                _ => Task.FromResult(true),
            };
            return true;
        }

        IStarterPackCatalog catalog = context.Factory.GetRequiredService<IStarterPackCatalog>();
        StarterPackDefinition pack = await catalog.GetAsync(packId, context.CancellationToken).ConfigureAwait(false);
        StarterPackProfileDefinition? profile = pack.Profiles
            .FirstOrDefault(candidate => string.Equals(candidate.Id, profileId, StringComparison.OrdinalIgnoreCase));
        string profileLabel = profile?.DisplayName ?? profileId;

        _picker = new TuiInlinePicker(
            $"Apply {pack.DisplayName} ({profileLabel})?",
            [
                (NoChoice, "Cancel"),
                (YesChoice, "Yes, apply pack"),
            ]);
        _pickerHandler = choice => choice switch
        {
            YesChoice => RunPackApplyAsync(context, packId, profileId, acceptVoiceCloningConsent: false),
            _ => Task.FromResult(true),
        };
        return true;
    }

    internal static bool NeedsVoiceCloningConsentPrompt(StarterPackSummary summary) =>
        summary.RequiresVoiceCloningConsent && !summary.CanApply;

    private async Task<bool> RunPackApplyAsync(
        TrackdubTuiContext context,
        string packId,
        string profileId,
        bool acceptVoiceCloningConsent)
    {
        StarterPackApplyResult result = await StarterPacksHandler
            .ApplyPackAsync(
                context.Factory,
                packId,
                profileId,
                acceptVoiceCloningConsent,
                context.CancellationToken)
            .ConfigureAwait(false);

        context.SetStatus(result.Success
            ? $"Applied starter pack {packId} ({profileId})."
            : result.FailureReason ?? $"Failed to apply starter pack {packId}.");
        return true;
    }

    private async Task<bool> RunPackDownloadAsync(TrackdubTuiContext context, string packId, string profileId)
    {
        bool success = false;
        bool cancelled = false;
        string? failureReason = null;

        try
        {
            await context.Console.Progress()
                .AutoClear(false)
                .Columns(
                [
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                ])
                .StartAsync(async progressContext =>
                {
                    ProgressTask task = progressContext.AddTask(
                        $"Downloading pack {packId}",
                        maxValue: 100);

                    var progress = new Progress<ModelDownloadProgress>(report =>
                    {
                        if (report.TotalBytes is > 0)
                        {
                            double percent = report.PercentComplete > 0
                                ? report.PercentComplete
                                : 100.0 * report.BytesDownloaded / report.TotalBytes.Value;
                            task.Value = Math.Clamp(percent, 0, 100);
                        }
                        else if (!string.IsNullOrWhiteSpace(report.Message))
                        {
                            task.Description = $"{packId}: {report.Message}";
                        }
                    });

                    StarterPackDownloadResult result = await StarterPacksHandler
                        .DownloadPackAsync(context.Factory, packId, profileId, progress, context.CancellationToken)
                        .ConfigureAwait(false);

                    success = result.Success;
                    cancelled = string.Equals(result.FailureReason, "Download cancelled.", StringComparison.Ordinal);
                    failureReason = result.FailureReason;
                    task.Value = cancelled ? task.Value : 100;
                })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        context.SetStatus(cancelled
            ? "Pack download cancelled."
            : success
                ? $"Downloaded starter pack {packId} ({profileId}). Settings were not changed."
                : failureReason ?? $"Pack download failed for {packId}.");
        return true;
    }

    private static string FormatPackStatus(string statusLabel) =>
        statusLabel switch
        {
            "applied" => "[cyan]applied[/]",
            "recommended" => "[green]recommended[/]",
            "license review needed" => "[yellow]license review needed[/]",
            _ => EscapeMarkup(statusLabel),
        };

    private async Task<bool> BeginDownloadMenuAsync(TrackdubTuiContext context)
    {
        _missingCandidates = await GetMissingCandidatesAsync(context).ConfigureAwait(false);

        if (_missingCandidates.Count == 0)
        {
            context.SetStatus("No missing commercial bundled models to download.");
            return true;
        }

        _picker = new TuiInlinePicker(
            "Ad-hoc download (any missing model)",
            [
                (BackChoice, "Cancel"),
                (AllMissingChoice, $"All missing ({_missingCandidates.Count})"),
                (PickOneChoice, "Choose one model…"),
            ]);
        _pickerHandler = choice => HandleDownloadMenuChoiceAsync(context, choice);
        return true;
    }

    private Task<bool> HandleDownloadMenuChoiceAsync(TrackdubTuiContext context, string choice) =>
        choice switch
        {
            BackChoice => Task.FromResult(true),
            AllMissingChoice => BeginDownloadAllConfirmAsync(context),
            PickOneChoice => BeginDownloadOnePickerAsync(context),
            _ => Task.FromResult(true),
        };

    private Task<bool> BeginDownloadOnePickerAsync(TrackdubTuiContext context)
    {
        var choices = new List<(string Value, string Label)>
        {
            (BackChoice, "Cancel"),
        };
        choices.AddRange(_missingCandidates.Select(entry =>
            (entry.ModelId, TuiMarkup.FormatModelLabel(entry))));

        _picker = new TuiInlinePicker("Download which model?", choices);
        _pickerHandler = choice => HandleDownloadOneChoiceAsync(context, choice);
        return Task.FromResult(true);
    }

    private async Task<bool> HandleDownloadOneChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return await BeginDownloadMenuAsync(context).ConfigureAwait(false);
        }

        ModelDownloadOutcome outcome = await RunDownloadWithProgressAsync(context, choice).ConfigureAwait(false);
        string label = TuiMarkup.FormatModelSlug(choice);
        context.SetStatus(outcome switch
        {
            ModelDownloadOutcome.Succeeded => $"Downloaded {label}.",
            ModelDownloadOutcome.Cancelled => $"Download cancelled for {label}.",
            _ => $"Download failed for {label}. See errors above.",
        });
        return true;
    }

    private async Task<bool> BeginDownloadAllConfirmAsync(TrackdubTuiContext context)
    {
        _missingCandidates = await GetMissingCandidatesAsync(context).ConfigureAwait(false);

        if (_missingCandidates.Count == 0)
        {
            context.SetStatus("No missing commercial bundled models to download.");
            return true;
        }

        _picker = new TuiInlinePicker(
            $"Download all {_missingCandidates.Count} missing commercial bundled models?",
            [
                (NoChoice, "Cancel"),
                (YesChoice, "Yes, download all"),
            ]);
        _pickerHandler = choice => HandleDownloadAllConfirmChoiceAsync(context, choice);
        return true;
    }

    private Task<bool> HandleDownloadAllConfirmChoiceAsync(TrackdubTuiContext context, string choice) =>
        choice switch
        {
            YesChoice => RunDownloadAllAsync(context),
            NoChoice => Task.FromResult(true),
            _ => Task.FromResult(true),
        };

    private async Task<bool> RunDownloadAllAsync(TrackdubTuiContext context)
    {
        bool allSucceeded = true;
        var wasCancelled = false;
        foreach (ModelInventoryEntry entry in _missingCandidates)
        {
            ModelDownloadOutcome outcome = await RunDownloadWithProgressAsync(context, entry.ModelId).ConfigureAwait(false);
            if (outcome == ModelDownloadOutcome.Cancelled)
            {
                wasCancelled = true;
                break;
            }

            if (outcome != ModelDownloadOutcome.Succeeded)
            {
                allSucceeded = false;
            }
        }

        context.SetStatus(wasCancelled
            ? "Download cancelled."
            : allSucceeded
                ? $"Downloaded {_missingCandidates.Count} models."
                : "One or more downloads failed.");
        return true;
    }

    private async Task<bool> BeginVerifyPickerAsync(TrackdubTuiContext context)
    {
        IReadOnlyList<ModelInventoryEntry> entries = await ModelsHandler
            .GetInventoryAsync(context.Factory, context.CancellationToken)
            .ConfigureAwait(false);

        List<string> modelIds = entries
            .Select(entry => entry.ModelId)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (modelIds.Count == 0)
        {
            context.SetStatus("No manifest models to verify.");
            return true;
        }

        var choices = new List<(string Value, string Label)>
        {
            (BackChoice, "Cancel"),
        };
        choices.AddRange(modelIds.Select(id => (id, TuiMarkup.FormatModelSlug(id))));

        _picker = new TuiInlinePicker("Verify which model?", choices);
        _pickerHandler = choice => HandleVerifyChoiceAsync(context, choice);
        return true;
    }

    private async Task<bool> HandleVerifyChoiceAsync(TrackdubTuiContext context, string choice)
    {
        if (choice == BackChoice)
        {
            return true;
        }

        await using var output = new StringWriter();
        int exitCode = await ModelsHandler.VerifyAsync(
            context.Factory,
            choice,
            output,
            context.CancellationToken).ConfigureAwait(false);

        context.SetStatus(exitCode == Program.ExitSuccess
            ? $"Verify passed for {TuiMarkup.FormatModelSlug(choice)}."
            : $"Verify failed for {TuiMarkup.FormatModelSlug(choice)}.");
        return true;
    }

    private enum ModelDownloadOutcome
    {
        Succeeded,
        Failed,
        Cancelled,
    }

    private static async Task<ModelDownloadOutcome> RunDownloadWithProgressAsync(TrackdubTuiContext context, string modelId)
    {
        bool success = false;
        bool cancelled = false;
        string? failureReason = null;
        string label = TuiMarkup.FormatModelSlug(modelId);

        try
        {
            await context.Console.Progress()
                .AutoClear(false)
                .Columns(
                [
                    new TaskDescriptionColumn(),
                    new ProgressBarColumn(),
                    new PercentageColumn(),
                ])
                .StartAsync(async progressContext =>
                {
                    ProgressTask task = progressContext.AddTask(
                        $"Downloading {label}",
                        maxValue: 100);

                    var progress = new Progress<ModelDownloadProgress>(report =>
                    {
                        if (report.TotalBytes is > 0)
                        {
                            double percent = report.PercentComplete > 0
                                ? report.PercentComplete
                                : 100.0 * report.BytesDownloaded / report.TotalBytes.Value;
                            task.Value = Math.Clamp(percent, 0, 100);
                        }
                        else if (!string.IsNullOrWhiteSpace(report.Message))
                        {
                            task.Description = $"{label}: {report.Message}";
                        }
                    });

                    ModelDownloadResult result = await ModelsHandler
                        .DownloadModelAsync(context.Factory, modelId, progress, context.CancellationToken)
                        .ConfigureAwait(false);

                    success = result.Success;
                    cancelled = result.Cancelled;
                    failureReason = result.FailureReason;
                    task.Value = cancelled ? task.Value : 100;
                })
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }

        if (cancelled)
        {
            return ModelDownloadOutcome.Cancelled;
        }

        if (!success)
        {
            CliErrorReporter.ReportError(
                ErrorCode.ModelNotAvailable,
                failureReason ?? $"Download failed for '{label}'.");
            return ModelDownloadOutcome.Failed;
        }

        return ModelDownloadOutcome.Succeeded;
    }

    private static string FormatState(ModelCacheState state) =>
        state switch
        {
            ModelCacheState.Ready or ModelCacheState.Installed =>
                $"[green]{state}[/]",
            ModelCacheState.Missing or ModelCacheState.Downloading =>
                $"[yellow]{state}[/]",
            ModelCacheState.Corrupt or ModelCacheState.Blocked =>
                $"[red]{state}[/]",
            _ => state.ToString(),
        };

    private static string EscapeMarkup(string value) =>
        value.Replace("[", "[[", StringComparison.Ordinal);
}
