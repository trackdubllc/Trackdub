using Trackdub.Contracts.Pipeline;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts.Pipeline;

internal static class PipelineProgressReporter
{
    public static void Started(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        string? message = null,
        string? phase = "Starting") =>
        Report(progress, stageKey, PipelineProgressEventKind.Started, message, phase: phase);

    public static void Phase(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        string phase,
        string? message = null,
        string? currentItemLabel = null) =>
        Report(
            progress,
            stageKey,
            PipelineProgressEventKind.Progress,
            message,
            phase: phase,
            currentItemLabel: currentItemLabel);

    public static void Determinate(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        int completedUnits,
        int totalUnits,
        string phase,
        string? message = null,
        string? currentItemLabel = null)
    {
        double? percentComplete = totalUnits > 0
            ? Math.Clamp((double)completedUnits / totalUnits * 100d, 0d, 100d)
            : null;
        Report(
            progress,
            stageKey,
            PipelineProgressEventKind.Progress,
            message,
            percentComplete,
            phase,
            completedUnits,
            totalUnits,
            currentItemLabel);
    }

    /// <summary>
    /// Reports that a pipeline output truthfully exists now (recognized segment, persisted
    /// segment, or committed playable audio). Emitted as <see cref="PipelineProgressEventKind.Progress"/>
    /// with phase "Output available"; callers distinguish it via <paramref name="outputKind"/>.
    /// </summary>
    public static void Output(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        PipelineOutputKind outputKind,
        int itemIndex,
        Guid? revisionId = null,
        Guid? segmentId = null,
        Guid? artifactId = null,
        string? message = null) =>
        progress?.Report(new PipelineProgressEvent(
            StageName: ResolveStageName(stageKey),
            EventKind: PipelineProgressEventKind.Progress,
            Message: message,
            StageKey: stageKey,
            Phase: "Output available",
            OutputKind: outputKind,
            ItemIndex: itemIndex,
            RevisionId: revisionId,
            SegmentId: segmentId,
            ArtifactId: artifactId));

    public static void Completed(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        TimeSpan elapsed,
        string? message = null) =>
        Report(progress, stageKey, PipelineProgressEventKind.Completed, message, 100d, elapsedDuration: elapsed);

    public static void Failed(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        string message,
        TimeSpan elapsed) =>
        Report(progress, stageKey, PipelineProgressEventKind.Failed, message, elapsedDuration: elapsed);

    public static void Skipped(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        string message,
        TimeSpan elapsed = default) =>
        Report(progress, stageKey, PipelineProgressEventKind.Skipped, message, elapsedDuration: elapsed);

    private static void Report(
        IProgress<PipelineProgressEvent>? progress,
        string stageKey,
        PipelineProgressEventKind eventKind,
        string? message,
        double? percentComplete = null,
        string? phase = null,
        int? completedUnits = null,
        int? totalUnits = null,
        string? currentItemLabel = null,
        TimeSpan elapsedDuration = default)
    {
        progress?.Report(new PipelineProgressEvent(
            StageName: ResolveStageName(stageKey),
            EventKind: eventKind,
            PercentComplete: percentComplete,
            Message: message,
            ElapsedDuration: elapsedDuration,
            StageKey: stageKey,
            Phase: phase,
            CompletedUnits: completedUnits,
            TotalUnits: totalUnits,
            CurrentItemLabel: currentItemLabel));
    }

    private static string ResolveStageName(string stageKey) =>
        stageKey switch
        {
            StageNames.Separation => "Separation",
            StageNames.Vad => "VAD",
            StageNames.Diarization => "Diarization",
            StageNames.Asr => "ASR",
            StageNames.SpeakerAssignment => "Transcript persistence",
            StageNames.SpeechEnhancement => "Cleanup",
            StageNames.Translation => "Translation",
            StageNames.Tts => "TTS",
            StageNames.Export => "Export",
            _ => stageKey
        };
}
