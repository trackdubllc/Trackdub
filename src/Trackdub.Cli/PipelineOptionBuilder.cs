using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli;

/// <summary>
/// Helper for building <see cref="DubbingSessionOptions"/> and <see cref="RunPipelineHandler.RunPipelineRequest"/>
/// from parsed CLI arguments. Eliminates duplication between DubCommand and RunCommand.
/// </summary>
internal static class PipelineOptionBuilder
{
    /// <summary>
    /// Builds a <see cref="DubbingSessionOptions"/> for batch processing.
    /// </summary>
    public static DubbingSessionOptions BuildBatchSessionOptions(
        string targetLanguage,
        string? sourceLanguage,
        string? outputDirectory,
        Dictionary<string, string>? modelPreferences,
        string? exportFormat,
        bool enableAsrTextRefinement,
        bool voiceClone,
        bool timbrePolish,
        bool restorePan,
        bool matchLoudness,
        Dictionary<string, string>? voiceOverrides,
        IReadOnlyList<string>? subtitleFormats,
        string? subtitleSource,
        bool burnInSubtitles,
        string? videoEncoderKey,
        IReadOnlyList<string>? stageFilter = null,
        bool forceRerun = false)
    {
        return new DubbingSessionOptions
        {
            SourceMediaPath = "batch", // Placeholder; BatchProcessor overrides per-file
            TargetLanguageCode = targetLanguage,
            SourceLanguageCode = sourceLanguage,
            ProjectOutputDirectory = outputDirectory is not null ? Path.GetFullPath(outputDirectory) : null,
            ModelPreferences = modelPreferences is { Count: > 0 } ? modelPreferences : null,
            ExportFormat = exportFormat,
            EnableAsrTextRefinement = enableAsrTextRefinement,
            UseVoiceCloning = voiceClone,
            ApplyTimbrePolish = timbrePolish,
            RestoreOriginalPan = restorePan,
            MatchOriginalLoudness = matchLoudness,
            VoiceAssignmentOverrides = voiceOverrides is { Count: > 0 } ? voiceOverrides : null,
            SubtitleFormats = subtitleFormats,
            SubtitleSource = subtitleSource,
            BurnInSubtitles = burnInSubtitles,
            VideoEncoder = VideoEncoderPreferenceSettings.FromKey(videoEncoderKey),
            StageFilter = stageFilter,
            ForceRerun = forceRerun,
        };
    }

    /// <summary>
    /// Builds a <see cref="RunPipelineHandler.RunPipelineRequest"/> for single-file processing.
    /// </summary>
    public static Handlers.RunPipelineHandler.RunPipelineRequest BuildSingleFileRequest(
        string sourceMediaPath,
        string projectOutputDirectory,
        string? sourceLanguage,
        string targetLanguage,
        Dictionary<string, string>? modelPreferences,
        string? exportFormat,
        bool enableAsrTextRefinement,
        bool voiceClone,
        bool timbrePolish,
        bool restorePan,
        bool matchLoudness,
        Dictionary<string, string>? voiceOverrides,
        IReadOnlyList<string>? subtitleFormats,
        string? subtitleSource,
        bool burnInSubtitles,
        string? videoEncoderKey,
        IReadOnlyList<string>? stageFilter = null,
        bool forceRerun = false)
    {
        return new Handlers.RunPipelineHandler.RunPipelineRequest
        {
            SourceMediaPath = sourceMediaPath,
            ProjectOutputDirectory = projectOutputDirectory,
            SourceLanguageCode = sourceLanguage,
            TargetLanguageCode = targetLanguage,
            ModelPreferences = modelPreferences is { Count: > 0 } ? modelPreferences : null,
            ExportFormat = exportFormat,
            StageFilter = stageFilter,
            ForceRerun = forceRerun,
            EnableAsrTextRefinement = enableAsrTextRefinement,
            UseVoiceCloning = voiceClone,
            ApplyTimbrePolish = timbrePolish,
            RestoreOriginalPan = restorePan,
            MatchOriginalLoudness = matchLoudness,
            VoiceAssignmentOverrides = voiceOverrides is { Count: > 0 } ? voiceOverrides : null,
            SubtitleFormats = subtitleFormats,
            SubtitleSource = subtitleSource,
            BurnInSubtitles = burnInSubtitles,
            VideoEncoder = VideoEncoderPreferenceSettings.FromKey(videoEncoderKey),
        };
    }
}

