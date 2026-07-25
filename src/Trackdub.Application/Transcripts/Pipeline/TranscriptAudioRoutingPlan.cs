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
