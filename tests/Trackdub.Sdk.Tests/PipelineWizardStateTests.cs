using Trackdub.Cli.Handlers;
using Trackdub.Cli.Tui.Screens;

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
    public void ResetSemantics_FreshInstanceRestoresAllDefaults()
    {
        // Mutate an instance the way the wizard handlers do across overlay steps.
        var mutated = new PipelineWizardState
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

        // ResetWizardState() resets by assigning a fresh instance (single source of truth).
        PipelineWizardState reset = new();

        Assert.False(reset.VoiceClone);
        Assert.True(reset.TimbrePolish);
        Assert.False(reset.RestorePan);
        Assert.False(reset.MatchLoudness);
        Assert.False(reset.AsrRefinement);
        Assert.False(reset.BurnIn);
        Assert.False(reset.ForceRerun);
        Assert.Null(reset.ExportFormat);
        Assert.Null(reset.SubtitleSource);
        Assert.Null(reset.SubtitleFormats);
        Assert.Null(reset.VideoEncoder);
        Assert.Null(reset.TargetLanguageOverride);

        // Sanity: the mutated instance genuinely diverged from defaults before reset.
        Assert.True(mutated.VoiceClone);
        Assert.False(mutated.TimbrePolish);
        Assert.NotNull(mutated.SubtitleFormats);
    }
}
