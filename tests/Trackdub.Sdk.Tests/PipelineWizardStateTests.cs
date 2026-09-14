using Spectre.Console.Testing;

using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui;
using Trackdub.Cli.Tui.Screens;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Locks in the encapsulated full-pipeline wizard state (<see cref="PipelineWizardState"/>)
/// and its single mapping to <see cref="PipelineHandler.TuiPipelineRunOptions"/> so that
/// future option additions cannot silently drift the declaration/reset/mapping lists.
/// </summary>
public sealed class PipelineWizardStateTests
{
    [Fact]
    public void ToRunOptions_AllOptionsConfigured_MapsEveryPropertyIndividually()
    {
        var state = new PipelineWizardState
        {
            VoiceClone = true,
            TimbrePolish = false,
            RestorePan = true,
            MatchLoudness = true,
            AsrRefinement = true,
            BurnIn = true,
            ForceRerun = true,
            ExportFormat = "mkv",
            SubtitleSource = "bilingual",
            SubtitleFormats = ["vtt"],
            VideoEncoder = "nvenc",
            TargetLanguageOverride = "es",
        };

        PipelineHandler.TuiPipelineRunOptions options = state.ToRunOptions();

        // Assert each property individually so a dropped or crossed mapping line fails here.
        Assert.True(options.UseVoiceCloning);
        Assert.False(options.ApplyTimbrePolish);
        Assert.True(options.RestoreOriginalPan);
        Assert.True(options.MatchOriginalLoudness);
        Assert.True(options.EnableAsrTextRefinement);
        Assert.True(options.BurnInSubtitles);
        Assert.True(options.ForceRerun);
        Assert.Equal("mkv", options.ExportFormat);
        Assert.Equal("bilingual", options.SubtitleSource);
        Assert.Equal(["vtt"], options.SubtitleFormats);
        Assert.Equal("nvenc", options.VideoEncoderKey);
        Assert.Equal("es", options.TargetLanguageOverride);
    }

    [Fact]
    public void ToRunOptions_FreshState_MatchesWizardDefaults()
    {
        PipelineHandler.TuiPipelineRunOptions options = new PipelineWizardState().ToRunOptions();

        // ApplyTimbrePolish defaults to on; every other configurable option is at its default.
        Assert.True(options.ApplyTimbrePolish);
        Assert.False(options.UseVoiceCloning);
        Assert.False(options.RestoreOriginalPan);
        Assert.False(options.MatchOriginalLoudness);
        Assert.False(options.EnableAsrTextRefinement);
        Assert.False(options.BurnInSubtitles);
        Assert.False(options.ForceRerun);
        Assert.Null(options.ExportFormat);
        Assert.Null(options.SubtitleSource);
        Assert.Null(options.SubtitleFormats);
        Assert.Null(options.VideoEncoderKey);
        Assert.Null(options.TargetLanguageOverride);
    }

    [Fact]
    public void FreshState_HasWizardDefaults()
    {
        var state = new PipelineWizardState();

        Assert.False(state.VoiceClone);
        Assert.True(state.TimbrePolish);
        Assert.False(state.RestorePan);
        Assert.False(state.MatchLoudness);
        Assert.False(state.AsrRefinement);
        Assert.False(state.BurnIn);
        Assert.False(state.ForceRerun);
        Assert.Null(state.ExportFormat);
        Assert.Null(state.SubtitleSource);
        Assert.Null(state.SubtitleFormats);
        Assert.Null(state.VideoEncoder);
        Assert.Null(state.TargetLanguageOverride);
    }

    [Fact]
    public async Task ConfigureFlow_ResetReplacesWizardStateWithDefaults()
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder().Build();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("es");
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = "project",
        };
        var screen = new PipelineTuiScreen();

        // Drive every one of the 12 wizard properties to a NON-DEFAULT value.
        await OpenConfigureWizardAsync(screen, context);

        // VoiceClone picker [No, Yes, Back] -> Yes => VoiceClone = true.
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // ExportFormat picker [auto, mp4, mkv, Back] -> mkv.
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // SubtitleFormat picker [srt, vtt, ass, none, skip, Back] -> vtt => ["vtt"].
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // SubtitleSource picker [translated, transcript, bilingual, Back] -> bilingual.
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // Advanced picker [Run, Timbre, Pan, Loudness, ASR, BurnIn, ForceRerun, Video, Back].
        // Each toggle selection re-opens Advanced with the index reset to 0, so navigate from
        // the top each time.
        // Timbre = index 1 => TimbrePolish flips true -> false.
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // Pan = index 2 => RestorePan flips false -> true.
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // Loudness = index 3 => MatchLoudness flips false -> true.
        await PressAsync(
            screen,
            context,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.Enter);

        // ASR = index 4 => AsrRefinement flips false -> true.
        await PressAsync(
            screen,
            context,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.Enter);

        // BurnIn = index 5 => BurnIn flips false -> true.
        await PressAsync(
            screen,
            context,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.Enter);

        // ForceRerun = index 6 => ForceRerun flips false -> true.
        await PressAsync(
            screen,
            context,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.Enter);

        // Video = index 7 => opens Video picker [auto, nvenc, qsv, amf, software,
        // videotoolbox, vaapi, Back]; nvenc = index 1 => VideoEncoder = "nvenc"
        // (returns to Advanced).
        await PressAsync(
            screen,
            context,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.DownArrow,
            ConsoleKey.Enter);
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.Enter);

        // Assert EVERY field is at its non-default value before reset.
        PipelineWizardState beforeReset = screen.WizardState;
        Assert.True(beforeReset.VoiceClone);
        Assert.False(beforeReset.TimbrePolish);
        Assert.True(beforeReset.RestorePan);
        Assert.True(beforeReset.MatchLoudness);
        Assert.True(beforeReset.AsrRefinement);
        Assert.True(beforeReset.BurnIn);
        Assert.True(beforeReset.ForceRerun);
        Assert.Equal("mkv", beforeReset.ExportFormat);
        Assert.Equal("bilingual", beforeReset.SubtitleSource);
        Assert.Equal(["vtt"], beforeReset.SubtitleFormats);
        Assert.Equal("nvenc", beforeReset.VideoEncoder);
        Assert.Equal("es", beforeReset.TargetLanguageOverride);

        // Re-enter Configure to force a reset. Queue a valid language so the wizard
        // proceeds past the target-language guard into BeginVoiceClonePickerAsync instead
        // of returning early on an empty language.
        await PressAsync(screen, context, ConsoleKey.Escape);
        console.Input.PushTextWithEnter("es");
        await OpenConfigureWizardAsync(screen, context);

        // The wizard genuinely re-activated: an overlay picker is present.
        Assert.True(screen.HasOverlay);

        // Assert EVERY field is back to its default after reset.
        PipelineWizardState afterReset = screen.WizardState;
        Assert.False(afterReset.VoiceClone);
        Assert.True(afterReset.TimbrePolish);
        Assert.False(afterReset.RestorePan);
        Assert.False(afterReset.MatchLoudness);
        Assert.False(afterReset.AsrRefinement);
        Assert.False(afterReset.BurnIn);
        Assert.False(afterReset.ForceRerun);
        Assert.Null(afterReset.ExportFormat);
        Assert.Null(afterReset.SubtitleSource);
        Assert.Null(afterReset.SubtitleFormats);
        Assert.Null(afterReset.VideoEncoder);

        // The second Configure attempt re-prompts for the target language and we queued
        // "es", so TargetLanguageOverride is populated again after reset.
        Assert.Equal("es", afterReset.TargetLanguageOverride);
    }

    [Fact]
    public async Task ConfigureFlow_SubtitleSentinelsPreserveNoneAndSkipSemantics()
    {
        PipelineWizardState none = await SelectSubtitleFormatAsync(3);
        Assert.NotNull(none.SubtitleFormats);
        Assert.Empty(none.SubtitleFormats!);

        PipelineWizardState skip = await SelectSubtitleFormatAsync(4);
        Assert.Null(skip.SubtitleFormats);
    }

    private static async Task<PipelineWizardState> SelectSubtitleFormatAsync(
        int downArrowCount)
    {
        using TrackdubSessionFactory factory = new TrackdubBuilder().Build();
        var console = new TestConsole();
        console.Profile.Capabilities.Interactive = true;
        console.Input.PushTextWithEnter("es");
        var context = new TrackdubTuiContext(factory, console, CancellationToken.None)
        {
            ProjectPath = "project",
        };
        var screen = new PipelineTuiScreen();

        await OpenConfigureWizardAsync(screen, context);
        await PressAsync(screen, context, ConsoleKey.Enter, ConsoleKey.Enter);
        for (int i = 0; i < downArrowCount; i++)
        {
            await PressAsync(screen, context, ConsoleKey.DownArrow);
        }
        await PressAsync(screen, context, ConsoleKey.Enter);
        await PressAsync(screen, context, ConsoleKey.Enter);
        return screen.WizardState;
    }

    private static async Task OpenConfigureWizardAsync(
        PipelineTuiScreen screen,
        TrackdubTuiContext context)
    {
        await PressAsync(screen, context, ConsoleKey.G, ConsoleKey.DownArrow, ConsoleKey.Enter);
    }

    private static async Task PressAsync(
        PipelineTuiScreen screen,
        TrackdubTuiContext context,
        params ConsoleKey[] keys)
    {
        foreach (ConsoleKey key in keys)
        {
            await screen.HandleKeyAsync(new ConsoleKeyInfo('\0', key, false, false, false), context);
        }
    }
}
