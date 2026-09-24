using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;

namespace Trackdub.Application.Transcripts.Pipeline;

public sealed record TranscriptAudioRoutingPlan(
    ProjectArtifact VadAudioArtifact,
    ProjectArtifact AsrAudioArtifact,
    ProjectArtifact DiarizationAudioArtifact,
    SpeechAudioSourceKind SourceKind,
    ProjectArtifact? AnalysisArtifact,
    SpeechAudioStageDecision VadDecision,
    SpeechAudioStageDecision AsrDecision,
    SpeechAudioStageDecision DiarizationDecision)
{
    public static TranscriptAudioRoutingPlan Raw(ProjectArtifact sourceArtifact, SpeechAudioSourceKind sourceKind) =>
        new(
            sourceArtifact,
            sourceArtifact,
            sourceArtifact,
            sourceKind,
            AnalysisArtifact: null,
            CreateRawDecision(SpeechPipelineStageKind.Vad, sourceKind),
            CreateRawDecision(SpeechPipelineStageKind.Asr, sourceKind),
            CreateRawDecision(SpeechPipelineStageKind.Diarization, sourceKind));

    /// <summary>
    /// Routes ASR to the unprocessed full mix. Speech enhancement (DeepFilterNet or the ffmpeg
    /// chain) and stem separation measurably degraded Parakeet-TDT and at best matched Qwen3-ASR,
    /// while VAD and diarization keep the enhanced audio.
    /// </summary>
    public TranscriptAudioRoutingPlan WithUnprocessedAsrSource(ProjectArtifact normalizedAudioArtifact) =>
        this with
        {
            AsrAudioArtifact = normalizedAudioArtifact,
            AsrDecision = CreateRawDecision(SpeechPipelineStageKind.Asr, SourceKind),
        };

    private static SpeechAudioStageDecision CreateRawDecision(
        SpeechPipelineStageKind stage,
        SpeechAudioSourceKind sourceKind)
    {
        SpeechAudioFilterSelection selection = SpeechAudioProcessingProfileCatalog.BuildFilterSelection(
            SpeechAudioProcessingProfileCatalog.NoneProfileId,
            []);
        return new SpeechAudioStageDecision(
            stage,
            sourceKind,
            selection.ProfileId,
            selection.ProfileVersion,
            selection.CatalogVersion,
            selection.FilterChain,
            selection.ProfileHash,
            RequiresProcessing: false,
            TriggeredDefects: []);
    }
}
