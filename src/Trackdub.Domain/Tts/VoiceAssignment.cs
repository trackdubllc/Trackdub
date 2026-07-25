namespace Trackdub.Domain.Tts;

public sealed record VoiceAssignment(
    Guid Id,
    Guid ProjectId,
    Guid SpeakerId,
    string VoiceModelId,
    string? VoiceVariant,
    bool RequiresConsent,
    DateTimeOffset CreatedAtUtc,
    bool IsFallback = false,
    Guid? ReferenceClipArtifactId = null)
{
    public static VoiceAssignment Create(
        Guid projectId,
        Guid speakerId,
        string voiceModelId,
        string? voiceVariant = null,
        bool requiresConsent = false,
        bool isFallback = false,
        Guid? referenceClipArtifactId = null)
    {
        if (projectId == Guid.Empty)
        {
            throw new ArgumentException("Project id is required.", nameof(projectId));
        }

        if (speakerId == Guid.Empty)
        {
            throw new ArgumentException("Speaker id is required.", nameof(speakerId));
        }

        if (string.IsNullOrWhiteSpace(voiceModelId))
        {
            throw new ArgumentException("Voice model id is required.", nameof(voiceModelId));
        }

        return new VoiceAssignment(
            Guid.NewGuid(),
            projectId,
            speakerId,
            voiceModelId.Trim(),
            string.IsNullOrWhiteSpace(voiceVariant) ? null : voiceVariant.Trim(),
            requiresConsent,
            DateTimeOffset.UtcNow,
            isFallback,
            NormalizeReferenceClipArtifactId(referenceClipArtifactId));
    }

    public static VoiceAssignment CreateFallback(
        Guid projectId,
        Guid speakerId,
        string voiceModelId,
        string? voiceVariant = null) =>
        Create(projectId, speakerId, voiceModelId, voiceVariant, requiresConsent: false, isFallback: true);

    public VoiceAssignment AssignVoice(
        string voiceModelId,
        string? voiceVariant = null,
        bool requiresConsent = false,
        Guid? referenceClipArtifactId = null)
    {
        if (string.IsNullOrWhiteSpace(voiceModelId))
        {
            throw new ArgumentException("Voice model id is required.", nameof(voiceModelId));
        }

        return this with
        {
            VoiceModelId = voiceModelId.Trim(),
            VoiceVariant = string.IsNullOrWhiteSpace(voiceVariant) ? null : voiceVariant.Trim(),
            RequiresConsent = requiresConsent,
            IsFallback = false,
            ReferenceClipArtifactId = NormalizeReferenceClipArtifactId(referenceClipArtifactId)
        };
    }

    public VoiceAssignment AssignReferenceClip(Guid referenceClipArtifactId)
    {
        if (referenceClipArtifactId == Guid.Empty)
        {
            throw new ArgumentException("Reference clip artifact id is required.", nameof(referenceClipArtifactId));
        }

        return this with { ReferenceClipArtifactId = referenceClipArtifactId };
    }

    public VoiceAssignment ClearReferenceClip() =>
        this with { ReferenceClipArtifactId = null };

    private static Guid? NormalizeReferenceClipArtifactId(Guid? referenceClipArtifactId) =>
        referenceClipArtifactId is Guid value && value != Guid.Empty
            ? value
            : null;
}
