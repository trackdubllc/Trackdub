namespace Trackdub.Contracts.Dubbing;

/// <summary>
/// Immutable configuration snapshot capturing all inputs required to execute
/// a full or partial dubbing pipeline run.
/// </summary>
public sealed record DubbingSessionOptions
{
    /// <summary>
    /// Path to the source media file (required).
    /// </summary>
    public required string SourceMediaPath { get; init; }

    /// <summary>
    /// Directory where the project is created or found.
    /// When null, derived from the source media path.
    /// </summary>
    public string? ProjectOutputDirectory { get; init; }

    /// <summary>
    /// BCP-47 language code for the source language.
    /// When null, the ASR stage auto-detects the source language.
    /// </summary>
    public string? SourceLanguageCode { get; init; }

    /// <summary>
    /// BCP-47 language code for the target language (required).
    /// </summary>
    public required string TargetLanguageCode { get; init; }

    /// <summary>
    /// Stage-specific model overrides. Keys are stage names, values are model aliases.
    /// When null, default model selection applies.
    /// </summary>
    public IReadOnlyDictionary<string, string>? ModelPreferences { get; init; }

    /// <summary>
    /// Preferred export container format. Supported values are "mp4" and "mkv".
    /// When null, the export stage uses the product default.
    /// </summary>
    public string? ExportFormat { get; init; }

    /// <summary>
    /// Optional voice assignment overrides per speaker.
    /// Keys are speaker identifiers, values are voice identifiers.
    /// </summary>
    public IReadOnlyDictionary<string, string>? VoiceAssignmentOverrides { get; init; }

    /// <summary>
    /// Optional subset of pipeline stages to execute.
    /// When null, all applicable stages execute in standard order.
    /// </summary>
    public IReadOnlyList<string>? StageFilter { get; init; }

    /// <summary>
    /// When true, optional Qwen ASR text polish runs after transcription.
    /// Defaults to false.
    /// </summary>
    public bool EnableAsrTextRefinement { get; init; }

    /// <summary>
    /// When true, re-executes all stages regardless of existing artifacts.
    /// Defaults to false.
    /// </summary>
    public bool ForceRerun { get; init; }
}
