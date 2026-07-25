namespace Trackdub.Domain.Artifacts;

public enum ArtifactKind
{
    Unknown = 0,
    NormalizedAudio = 1,
    WaveformSummary = 2,
    SpeechRegions = 3,
    TranscriptRevision = 4,
    TranslationRevision = 5,
    ReferenceClip = 6,
    TtsTake = 7,
    Vocals = 8,
    Ambiance = 9,
    SpeechEnhancedAudio = 10,
    PreviewMix = 11,
    AudioQualityAnalysis = 12,
    SpeechProcessedAudio = 13,
    ExportManifest = 14,
    ExportAudio = 15,
    ExportVideo = 16,
    PipelineDegradation = 17,
    StemSeparationSourceAudio = 18,
    Music = 19,
    SoundEffects = 20,
    DiarizationResult = 21,
    LipSyncTake = 22,
    OverlapSourceCandidate = 23,
    OverlapRescueMetadata = 24,
    OverlapRescueCandidateTranscript = 25,
    LipSynthesisTake = 26
}

public sealed record ProjectArtifact(
    Guid Id,
    Guid ProjectId,
    Guid MediaAssetId,
    ArtifactKind Kind,
    string RelativePath,
    string Sha256,
    long SizeBytes,
    double? DurationSeconds,
    int? SampleRate,
    int? ChannelCount,
    DateTimeOffset CreatedAtUtc,
    Guid? StageRunId = null,
    string? Provenance = null,
    string? DegradationCode = null,
    string? DegradationStage = null);
