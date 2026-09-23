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
    /// Routes ASR to the unprocessed source (normalized mix or vocal stem). The ffmpeg speech
    /// enhancement chain (afftdn denoise + speechnorm expansion) reshapes the speech envelope and
    /// measurably degraded both Qwen3-ASR and Parakeet-TDT transcripts, while VAD and diarization
    /// keep the enhanced audio.
    /// </summary>
    /// <remarks>
    /// Follows the plan's own <see cref="SourceKind"/>, so a vocal stem the preparation planner
    /// rejected never comes back through the ASR route.
    /// </remarks>
    public TranscriptAudioRoutingPlan WithUnprocessedAsrSource(
        ProjectArtifact normalizedAudioArtifact,
        ProjectArtifact? vocalStemArtifact) =>
        this with
        {
            AsrAudioArtifact = SourceKind == SpeechAudioSourceKind.VocalStem && vocalStemArtifact is not null
                ? vocalStemArtifact
                : normalizedAudioArtifact,
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
