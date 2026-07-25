using Trackdub.Contracts;
using Trackdub.Domain.AudioQuality;

namespace Trackdub.Application.Transcripts;

public sealed class SpeechAudioPreparationPlanner : ISpeechAudioPreparationPlanner
{
    public SpeechAudioPreparationPlan Plan(SpeechAudioPreparationPlanningRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        AudioQualityAnalysisResult selectedAnalysis = request.FullMixAnalysis;
        SpeechAudioSourceKind selectedSourceKind = SpeechAudioSourceKind.FullMix;
        bool selectedSourceRejected = false;
        string? sourceRejectionReason = null;

        if (request.VocalStemAnalysis is not null)
        {
            string? vocalRejectionReason = GetVocalStemRejectionReason(
                request.MediaAsset.DurationSeconds,
                request.VocalStemAnalysis);
            if (vocalRejectionReason is null)
            {
                selectedAnalysis = request.VocalStemAnalysis;
                selectedSourceKind = SpeechAudioSourceKind.VocalStem;
            }
            else
            {
                selectedSourceRejected = true;
                sourceRejectionReason = vocalRejectionReason;
            }
        }

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
            selectedSourceRejected,
            sourceRejectionReason,
            selectedAnalysis,
            request.FullMixAnalysis,
            request.VocalStemAnalysis,
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
        string profileId = SelectProfileId(stage, sourceKind, defects);
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
        SpeechAudioSourceKind sourceKind,
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

        if (sourceKind is SpeechAudioSourceKind.VocalStem)
        {
            if (hasLowSnr || hasHiss)
            {
                return SpeechAudioProcessingProfileCatalog.VocalDenoiseLightProfileId;
            }

            if (hasRumble)
            {
                return SpeechAudioProcessingProfileCatalog.VocalRumbleCutProfileId;
            }

            return hasLowVolume
                ? SpeechAudioProcessingProfileCatalog.VocalGainSafeProfileId
                : SpeechAudioProcessingProfileCatalog.NoneProfileId;
        }

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

    private static string? GetVocalStemRejectionReason(
        double mediaDurationSeconds,
        AudioQualityAnalysisResult vocalAnalysis)
    {
        double durationDelta = Math.Abs(vocalAnalysis.Metrics.DurationSeconds - mediaDurationSeconds);
        if (durationDelta > vocalAnalysis.Thresholds.VocalStemDurationMismatchSeconds)
        {
            return $"Vocal stem duration differs from source by {durationDelta:F3}s.";
        }

        if (vocalAnalysis.Metrics.ActiveRmsDbfs < AudioQualityPolicy.UnusableActiveRmsDbfs)
        {
            return $"Vocal stem active RMS is {vocalAnalysis.Metrics.ActiveRmsDbfs:F1} dBFS.";
        }

        if (vocalAnalysis.Metrics.ClippedSamplePercent > AudioQualityPolicy.UnusableClippingPercent)
        {
            return $"Vocal stem clipping is {vocalAnalysis.Metrics.ClippedSamplePercent:F3}%.";
        }

        // Rumble, Hiss, and LowSnr all have vocal-stem remediation profiles; reject only
        // PoorSpeechBand since no profile can recover a stem that fundamentally lacks speech energy.
        AudioQualityDefectKind[] unusableDefects =
        [
            AudioQualityDefectKind.PoorSpeechBand
        ];
        AudioQualityDefectKind? unusableDefect = vocalAnalysis.TriggeredDefects
            .Where(defect => unusableDefects.Contains(defect))
            .Select<AudioQualityDefectKind, AudioQualityDefectKind?>(static defect => defect)
            .FirstOrDefault();
        if (unusableDefect is not null)
        {
            return $"Vocal stem quality analysis flagged {unusableDefect.Value}.";
        }

        if (vocalAnalysis.Metrics.SpeechBandRatioDb < AudioQualityPolicy.UnusableSpeechBandRatioDb)
        {
            return $"Vocal stem speech-band ratio is {vocalAnalysis.Metrics.SpeechBandRatioDb:F1} dB.";
        }

        return null;
    }
}
