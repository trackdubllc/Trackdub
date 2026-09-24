using Trackdub.Contracts;
using Trackdub.Domain.AudioQuality;

namespace Trackdub.Application.Transcripts;

public sealed class SpeechAudioPreparationPlanner : ISpeechAudioPreparationPlanner
{
    public SpeechAudioPreparationPlan Plan(SpeechAudioPreparationPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        AudioQualityAnalysisResult selectedAnalysis = request.FullMixAnalysis;
        const SpeechAudioSourceKind selectedSourceKind = SpeechAudioSourceKind.FullMix;

        SpeechAudioStageDecision vad = BuildStageDecision(
            SpeechPipelineStageKind.Vad,
            selectedSourceKind,
            selectedAnalysis);
        SpeechAudioStageDecision asr = BuildStageDecision(
            SpeechPipelineStageKind.Asr,
            selectedSourceKind,
            selectedAnalysis);
        SpeechAudioStageDecision diarization = BuildStageDecision(
            SpeechPipelineStageKind.Diarization,
            selectedSourceKind,
            selectedAnalysis);

        return new SpeechAudioPreparationPlan(
            selectedSourceKind,
            selectedAnalysis,
            vad,
            asr,
            diarization);
    }

    private static SpeechAudioStageDecision BuildStageDecision(
        SpeechPipelineStageKind stage,
        SpeechAudioSourceKind sourceKind,
        AudioQualityAnalysisResult analysis)
    {
        IReadOnlyList<AudioQualityDefectKind> defects = NormalizeDefectsForProcessing(analysis).ToArray();
        string profileId = SelectProfileId(stage, defects);
        SpeechAudioFilterSelection filterSelection = SpeechAudioProcessingProfileCatalog.BuildFilterSelection(
            profileId,
            defects);
        bool requiresProcessing = filterSelection.ProfileId != SpeechAudioProcessingProfileCatalog.NoneProfileId &&
                                  !string.IsNullOrWhiteSpace(filterSelection.FilterChain) &&
                                  filterSelection.IsAutoSelectable &&
                                  !filterSelection.IsBenchmarkOnly;

        return new SpeechAudioStageDecision(
            stage,
            sourceKind,
            filterSelection.ProfileId,
            filterSelection.ProfileVersion,
            filterSelection.CatalogVersion,
            filterSelection.FilterChain,
            filterSelection.ProfileHash,
            requiresProcessing,
            defects);
    }

    private static IEnumerable<AudioQualityDefectKind> NormalizeDefectsForProcessing(AudioQualityAnalysisResult analysis)
    {
        foreach (AudioQualityDefectKind defect in analysis.TriggeredDefects)
        {
            if (defect is AudioQualityDefectKind.LowSnr &&
                analysis.Metrics.SnrConfidence is not AudioSnrConfidence.Reliable)
            {
                continue;
            }

            if (defect is AudioQualityDefectKind.NearSilence or AudioQualityDefectKind.DurationMismatch)
            {
                continue;
            }

            yield return defect;
        }
    }

    private static string SelectProfileId(
        SpeechPipelineStageKind stage,
        IReadOnlyCollection<AudioQualityDefectKind> defects)
    {
        if (defects.Count == 0)
        {
            return SpeechAudioProcessingProfileCatalog.NoneProfileId;
        }

        bool hasRumble = defects.Contains(AudioQualityDefectKind.Rumble);
        bool hasHiss = defects.Contains(AudioQualityDefectKind.Hiss);
        bool hasLowSnr = defects.Contains(AudioQualityDefectKind.LowSnr);
        bool hasLowVolume = defects.Contains(AudioQualityDefectKind.LowVolume);

        return stage switch
        {
            SpeechPipelineStageKind.Vad when hasRumble || hasLowVolume =>
                SpeechAudioProcessingProfileCatalog.FullMixVadLightProfileId,
            SpeechPipelineStageKind.Asr when hasRumble || hasHiss || hasLowSnr || hasLowVolume =>
                SpeechAudioProcessingProfileCatalog.FullMixAsrLightProfileId,
            SpeechPipelineStageKind.Diarization when hasRumble || hasLowVolume || hasLowSnr =>
                SpeechAudioProcessingProfileCatalog.FullMixDiarizationSafeProfileId,
            _ => SpeechAudioProcessingProfileCatalog.NoneProfileId
        };
    }
}
