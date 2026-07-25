namespace Trackdub.Contracts.Pipeline;

public sealed record TtsSynthesisRequest(
    string Text,
    string LanguageCode,
    VoiceCatalogEntry Voice,
    float Speed = 1.0f,
    string? PhonemeOverride = null,
    InferenceRequestOptions? Options = null,
    VoiceCloneReference? VoiceCloneReference = null,
    double? TargetDurationSeconds = null);

public sealed record VoiceCloneReference(
    Guid SpeakerId,
    Guid ReferenceClipArtifactId,
    string ReferenceClipPath,
    double TotalDurationSeconds,
    double ActiveSpeechSeconds,
    string? ReferenceTranscript = null);
