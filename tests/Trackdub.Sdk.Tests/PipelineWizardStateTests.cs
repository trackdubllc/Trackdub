using System.Reflection;

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

        await OpenConfigureWizardAsync(screen, context);
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.Enter);
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.Enter);
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);
        await PressAsync(screen, context, ConsoleKey.DownArrow, ConsoleKey.DownArrow, ConsoleKey.Enter);

        PipelineWizardState beforeReset = GetWizardState(screen);
        Assert.True(beforeReset.VoiceClone);
        Assert.Equal("mkv", beforeReset.ExportFormat);
        Assert.Equal(["vtt"], beforeReset.SubtitleFormats);

        await PressAsync(screen, context, ConsoleKey.Escape);
        console.Input.PushTextWithEnter(string.Empty);
        await OpenConfigureWizardAsync(screen, context);

        PipelineWizardState afterReset = GetWizardState(screen);
        Assert.False(afterReset.VoiceClone);
        Assert.True(afterReset.TimbrePolish);
        Assert.Null(afterReset.ExportFormat);
        Assert.Null(afterReset.SubtitleFormats);
        Assert.Null(afterReset.TargetLanguageOverride);
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
        return GetWizardState(screen);
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

    private static PipelineWizardState GetWizardState(PipelineTuiScreen screen) =>
        (PipelineWizardState)typeof(PipelineTuiScreen)
            .GetField("_wizard", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(screen)!;
}
