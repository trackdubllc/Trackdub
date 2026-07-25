using Trackdub.Domain.StageRuns;

using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Canonical pipeline stage metadata for headless and CLI callers.
/// </summary>
public static class DubbingPipelineStages
{
    /// <summary>
    /// Canonical pipeline stage execution order for a full dubbing run.
    /// Lip stages are intentionally omitted — they are opt-in via StageFilter.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultStageOrder =
    [
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Diarization,
        StageNames.Asr,
        StageNames.Translation,
        StageNames.Tts,
        StageNames.Export,
    ];

    /// <summary>
    /// Full stage catalog used when resolving StageFilter.
    /// Lip-sync runs after TTS (takes exist). Lip-synthesis runs after Export (needs ExportAudio).
    /// </summary>
    public static readonly IReadOnlyList<string> ExtendedStageOrder =
    [
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Diarization,
        StageNames.Asr,
        StageNames.Translation,
        StageNames.Tts,
        StageNames.LipSync,
        StageNames.Export,
        StageNames.LipSynthesis,
    ];

    /// <summary>
    /// Stages that block all subsequent stages when they fail.
    /// </summary>
    public static readonly IReadOnlySet<string> PrerequisiteStages = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        StageNames.Vad,
        StageNames.Asr,
        StageNames.Translation,
        StageNames.Tts,
    };

    private static readonly HashSet<string> StagesRequiringSourceMedia = new(StringComparer.OrdinalIgnoreCase)
    {
        StageNames.Separation,
        StageNames.Vad,
        StageNames.Asr,
        StageNames.Diarization,
        StageNames.LipSynthesis,
    };

    private static readonly HashSet<string> StagesRequiringTargetLanguage = new(StringComparer.OrdinalIgnoreCase)
    {
        StageNames.Translation,
        StageNames.Tts,
    };

    /// <summary>
    /// Returns true when the stage reads from the original source media file directly.
    /// </summary>
    public static bool RequiresSourceMedia(string stageName) =>
        StagesRequiringSourceMedia.Contains(stageName);

    /// <summary>
    /// Returns true when the stage's execution logic consumes the session's target
    /// language code. Translation and Tts require it; source analysis and downstream
    /// artifact stages do not independently require a target language selection.
    /// </summary>
    public static bool RequiresTargetLanguage(string stageName) =>
        StagesRequiringTargetLanguage.Contains(stageName);
}
