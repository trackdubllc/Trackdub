using Trackdub.Cli.Handlers;

namespace Trackdub.Cli.Tui.Screens;

/// <summary>
/// Encapsulates the accumulating configuration for the full-pipeline "run all"
/// wizard. A single instance is carried through the overlay steps, mutated as
/// the user makes picker choices, reset by assigning a fresh instance, and
/// mapped to <see cref="PipelineHandler.TuiPipelineRunOptions"/> in one place
/// via <see cref="ToRunOptions"/>.
/// </summary>
internal sealed class PipelineWizardState
{
    public bool VoiceClone { get; set; }

    // Defaults to true to match TuiPipelineRunOptions.ApplyTimbrePolish.
    public bool TimbrePolish { get; set; } = true;

    public bool RestorePan { get; set; }

    public bool MatchLoudness { get; set; }

    public bool AsrRefinement { get; set; }

    public bool BurnIn { get; set; }

    public bool ForceRerun { get; set; }

    public string? ExportFormat { get; set; }

    public string? SubtitleSource { get; set; }

    public IReadOnlyList<string>? SubtitleFormats { get; set; }

    public string? VideoEncoder { get; set; }

    public string? TargetLanguageOverride { get; set; }

    public PipelineHandler.TuiPipelineRunOptions ToRunOptions() => new()
    {
        UseVoiceCloning = VoiceClone,
        ApplyTimbrePolish = TimbrePolish,
        RestoreOriginalPan = RestorePan,
        MatchOriginalLoudness = MatchLoudness,
        EnableAsrTextRefinement = AsrRefinement,
        BurnInSubtitles = BurnIn,
        ForceRerun = ForceRerun,
        ExportFormat = ExportFormat,
        SubtitleFormats = SubtitleFormats,
        SubtitleSource = SubtitleSource,
        VideoEncoderKey = VideoEncoder,
        TargetLanguageOverride = TargetLanguageOverride,
    };
}
