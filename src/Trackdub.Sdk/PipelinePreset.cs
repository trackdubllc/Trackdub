using System.Collections.Immutable;

namespace Trackdub.Sdk;

/// <summary>
/// Immutable snapshot of pipeline settings that can be persisted as a named preset.
/// </summary>
public sealed record PipelinePreset
{
    /// <summary>
    /// Maximum preset schema version this version of Trackdub can read and understand.
    /// Saved presets must be written with this value.
    /// </summary>
    public const int CurrentVersion = 1;

    /// <summary>
    /// Schema version of the preset file format.
    /// </summary>
    public required int Version { get; init; }

    /// <summary>
    /// BCP-47 language code for the target dubbing language.
    /// </summary>
    public required string TargetLanguage { get; init; }

    /// <summary>
    /// BCP-47 language code for the source language.
    /// When null, the ASR stage auto-detects the source language.
    /// </summary>
    public string? SourceLanguage { get; init; }

    private IReadOnlyDictionary<string, string>? _models;

    /// <summary>
    /// Stage-specific model overrides. Keys are stage names, values are model aliases.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Models
    {
        get => _models;
        init => _models = value is not null ? value.ToImmutableDictionary() : null;
    }

    /// <summary>
    /// Preferred export container format (e.g., "mp4", "mkv", "wav").
    /// </summary>
    public string? ExportFormat { get; init; }

    /// <summary>
    /// ONNX execution provider preference (e.g., "auto", "cpu", "directml", "cuda").
    /// Must be one of the values accepted by <c>CliParseHelpers.TryParseExecutionProvider</c>;
    /// an empty value means "use the application default".
    /// </summary>
    public string? ExecutionProvider { get; init; }

    /// <summary>
    /// Device selection policy (e.g., "explicit", "max-performance", "prefer-npu", "max-efficiency", "min-overall-power").
    /// Must be one of the values accepted by <c>WindowsMlExecutionDevicePolicySettings.TryParseKey</c>;
    /// an empty value means "use the application default".
    /// </summary>
    public string? DevicePolicy { get; init; }

    /// <summary>
    /// When true, optional ASR text refinement runs after transcription.
    /// </summary>
    public bool? EnableAsrTextRefinement { get; init; }
}
