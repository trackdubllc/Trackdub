using System.Security.Cryptography;
using System.Text;

namespace Trackdub.Domain.AudioQuality;

public enum SpeechAudioSourceKind
{
    FullMix,
    VocalStem
}

public enum SpeechPipelineStageKind
{
    Vad,
    Asr,
    Diarization
}

public enum AudioQualityAnalysisConfidence
{
    Low,
    Medium,
    High
}

public enum AudioSnrConfidence
{
    Unavailable,
    Estimated,
    Reliable
}

public enum AudioQualityDefectKind
{
    LowVolume,
    Clipping,
    LowSnr,
    Rumble,
    Hiss,
    PoorSpeechBand,
    NearSilence,
    DurationMismatch
}

public sealed record AudioQualityMetrics(
    double DurationSeconds,
    double PeakDbfs,
    double RmsDbfs,
    double ActiveRmsDbfs,
    double? Lufs,
    AudioQualityAnalysisConfidence AnalysisConfidence,
    SpeechAudioSourceKind SourceKind,
    double ClippedSamplePercent,
    double NearSilencePercent,
    double DcOffset,
    double RumbleRatioDb,
    double HissRatioDb,
    double SpeechBandRatioDb,
    double CrestFactorDb,
    double DynamicRangeDb,
    double? NoiseFloorDbfs,
    double? SnrDb,
    AudioSnrConfidence SnrConfidence);

public sealed record AudioQualityAnalysisThresholds(
    double LowVolumeActiveRmsDbfs,
    double LowVolumePeakDbfs,
    double ClippingPercent,
    double LowSnrDb,
    double RumbleRatioDb,
    double HissRatioDb,
    double PoorSpeechBandRatioDb,
    double NearSilenceActiveRmsDbfs,
    double VocalStemDurationMismatchSeconds = AudioQualityPolicy.DefaultVocalStemDurationMismatchSeconds)
{
    public static AudioQualityAnalysisThresholds ForSource(SpeechAudioSourceKind sourceKind) =>
        sourceKind is SpeechAudioSourceKind.VocalStem
            ? AudioQualityPolicy.VocalStemThresholds
            : AudioQualityPolicy.FullMixThresholds;
}

public sealed record AudioQualityAnalysisResult(
    string AudioPath,
    AudioQualityMetrics Metrics,
    AudioQualityAnalysisThresholds Thresholds,
    IReadOnlyList<AudioQualityDefectKind> TriggeredDefects,
    IReadOnlyList<string> Warnings);

public sealed record SpeechAudioProcessingProfile(
    string ProfileId,
    int ProfileVersion,
    string DisplayName,
    bool IsAutoSelectable,
    bool IsBenchmarkOnly,
    string BaseFilterChain);

public sealed record SpeechAudioFilterSelection(
    string ProfileId,
    int ProfileVersion,
    string CatalogVersion,
    string FilterChain,
    string ProfileHash,
    bool IsAutoSelectable,
    bool IsBenchmarkOnly);

public sealed record SpeechAudioStageDecision(
    SpeechPipelineStageKind Stage,
    SpeechAudioSourceKind SourceKind,
    string ProfileId,
    int ProfileVersion,
    string CatalogVersion,
    string FilterChain,
    string ProfileHash,
    bool RequiresProcessing,
    IReadOnlyList<AudioQualityDefectKind> TriggeredDefects,
    string? FallbackReason = null,
    Guid? OutputArtifactId = null,
    string? OutputRelativePath = null,
    AudioQualityAnalysisResult? ProcessedAnalysis = null);

public sealed record SpeechAudioPreparationPlan(
    SpeechAudioSourceKind SelectedSourceKind,
    bool SelectedSourceRejected,
    string? SourceRejectionReason,
    AudioQualityAnalysisResult SelectedSourceAnalysis,
    AudioQualityAnalysisResult FullMixAnalysis,
    AudioQualityAnalysisResult? VocalStemAnalysis,
    SpeechAudioStageDecision VadDecision,
    SpeechAudioStageDecision AsrDecision,
    SpeechAudioStageDecision DiarizationDecision);

public static class AudioQualityPolicy
{
    public const string AnalyzerPolicyVersion = "2026.04.1";
    public const double DefaultVocalStemDurationMismatchSeconds = 0.750d;
    public const double ProcessedDurationDriftRejectSeconds = 0.050d;
    public const double ProcessedClippingIncreaseRejectPercent = 0.050d;
    public const double ProcessedActiveRmsRejectDbfs = -14.0d;
    public const double ProcessedSpeechBandWorsenRejectDb = 2.0d;
    public const double DenoiseMinimumSnrImprovementDb = 2.0d;
    public const double UnusableActiveRmsDbfs = -48.0d;
    public const double UnusableClippingPercent = 1.0d;
    public const double UnusableSpeechBandRatioDb = -18.0d;

    public static AudioQualityAnalysisThresholds FullMixThresholds { get; } = new(
        LowVolumeActiveRmsDbfs: -32.0d,
        LowVolumePeakDbfs: -10.0d,
        ClippingPercent: 0.05d,
        LowSnrDb: 12.0d,
        RumbleRatioDb: -18.0d,
        HissRatioDb: -20.0d,
        PoorSpeechBandRatioDb: -8.0d,
        NearSilenceActiveRmsDbfs: -48.0d);

    public static AudioQualityAnalysisThresholds VocalStemThresholds { get; } = new(
        LowVolumeActiveRmsDbfs: -36.0d,
        LowVolumePeakDbfs: -12.0d,
        ClippingPercent: 0.03d,
        LowSnrDb: 15.0d,
        RumbleRatioDb: -15.0d,
        HissRatioDb: -16.0d,
        PoorSpeechBandRatioDb: -6.0d,
        NearSilenceActiveRmsDbfs: -48.0d);
}

public static class SpeechAudioProcessingProfileCatalog
{
    public const string CatalogVersion = "2026.04.1";
    public const string NoneProfileId = "none";
    public const string FullMixVadLightProfileId = "fullmix-vad-light";
    public const string FullMixAsrLightProfileId = "fullmix-asr-light";
    public const string FullMixDiarizationSafeProfileId = "fullmix-diarization-safe";
    public const string VocalRumbleCutProfileId = "vocal-rumble-cut";
    public const string VocalGainSafeProfileId = "vocal-gain-safe";
    public const string VocalDenoiseLightProfileId = "vocal-denoise-light";
    public const string CurrentAggressiveProfileId = "current-aggressive";

    private static readonly IReadOnlyDictionary<string, SpeechAudioProcessingProfile> Profiles =
        new[]
        {
            new SpeechAudioProcessingProfile(NoneProfileId, 1, "No processing", true, false, string.Empty),
            new SpeechAudioProcessingProfile(FullMixVadLightProfileId, 1, "Full mix VAD light cleanup", true, false, string.Empty),
            new SpeechAudioProcessingProfile(FullMixAsrLightProfileId, 1, "Full mix ASR light cleanup", true, false, string.Empty),
            new SpeechAudioProcessingProfile(FullMixDiarizationSafeProfileId, 1, "Full mix diarization safe cleanup", true, false, string.Empty),
            new SpeechAudioProcessingProfile(VocalRumbleCutProfileId, 1, "Vocal stem rumble cut", true, false, "highpass=f=70"),
            new SpeechAudioProcessingProfile(VocalGainSafeProfileId, 1, "Vocal stem safe gain", true, false, "volume=1.5"),
            new SpeechAudioProcessingProfile(VocalDenoiseLightProfileId, 1, "Vocal stem light denoise", true, false, "afftdn=nr=4:nf=-60"),
            new SpeechAudioProcessingProfile(CurrentAggressiveProfileId, 1, "Current aggressive benchmark baseline", false, true, "highpass=f=80,lowpass=f=8000,afftdn=nr=8:nf=-55,speechnorm=e=6.25:l=1")
        }.ToDictionary(profile => profile.ProfileId, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyCollection<SpeechAudioProcessingProfile> All => Profiles.Values.ToArray();

    public static SpeechAudioProcessingProfile GetRequired(string profileId) =>
        Profiles.TryGetValue(profileId, out SpeechAudioProcessingProfile? profile)
            ? profile
            : throw new ArgumentException($"Unknown speech audio processing profile '{profileId}'.", nameof(profileId));

    public static SpeechAudioFilterSelection BuildFilterSelection(
        string profileId,
        IEnumerable<AudioQualityDefectKind> defects)
    {
        SpeechAudioProcessingProfile profile = GetRequired(profileId);
        string filterChain = BuildFilterChain(profile, defects.Distinct().ToArray());
        string hash = ComputeProfileHash(profile.ProfileId, profile.ProfileVersion, filterChain);
        return new SpeechAudioFilterSelection(
            profile.ProfileId,
            profile.ProfileVersion,
            CatalogVersion,
            filterChain,
            hash,
            profile.IsAutoSelectable,
            profile.IsBenchmarkOnly);
    }

    private static string BuildFilterChain(
        SpeechAudioProcessingProfile profile,
        IReadOnlyCollection<AudioQualityDefectKind> defects)
    {
        if (!string.IsNullOrWhiteSpace(profile.BaseFilterChain))
        {
            return profile.BaseFilterChain;
        }

        var filters = new List<string>();
        bool hasRumble = defects.Contains(AudioQualityDefectKind.Rumble);
        bool hasHiss = defects.Contains(AudioQualityDefectKind.Hiss);
        bool hasLowSnr = defects.Contains(AudioQualityDefectKind.LowSnr);
        bool hasLowVolume = defects.Contains(AudioQualityDefectKind.LowVolume);

        switch (profile.ProfileId)
        {
            case FullMixVadLightProfileId:
                if (hasRumble)
                {
                    filters.Add("highpass=f=80");
                }
                if (hasLowVolume)
                {
                    filters.Add("volume=1.5");
                }
                break;

            case FullMixAsrLightProfileId:
                if (hasRumble)
                {
                    filters.Add("highpass=f=80");
                }
                if (hasHiss)
                {
                    filters.Add("lowpass=f=8000");
                }
                if (hasLowSnr || hasHiss)
                {
                    filters.Add("afftdn=nr=4:nf=-60");
                }
                if (hasLowVolume)
                {
                    filters.Add("volume=1.5");
                }
                break;

            case FullMixDiarizationSafeProfileId:
                if (hasRumble || hasLowSnr)
                {
                    filters.Add("highpass=f=80");
                }
                if (hasLowVolume)
                {
                    filters.Add("volume=1.25");
                }
                break;
        }

        return string.Join(',', filters);
    }

    private static string ComputeProfileHash(string profileId, int profileVersion, string filterChain)
    {
        string value = string.Join('|', CatalogVersion, profileId, profileVersion.ToString(System.Globalization.CultureInfo.InvariantCulture), filterChain);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
