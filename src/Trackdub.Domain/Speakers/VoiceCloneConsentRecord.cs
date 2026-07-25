namespace Trackdub.Domain.Speakers;

public sealed record VoiceCloneConsentRecord(
    Guid Id,
    Guid ProjectId,
    Guid SpeakerId,
    DateTimeOffset GrantedAtUtc,
    string ConsentVersion,
    bool IsThirdPartyConsent,
    string? Notes,
    DateTimeOffset? ExpiresAtUtc,
    DateTimeOffset? RevokedAtUtc)
{
    public const string CurrentVersion = "v1";

    public bool IsActive =>
        RevokedAtUtc is null &&
        (ExpiresAtUtc is null || ExpiresAtUtc.Value > DateTimeOffset.UtcNow);

    public static VoiceCloneConsentRecord Create(
        Guid projectId,
        Guid speakerId,
        bool isThirdPartyConsent,
        string? notes) =>
        new(
            Guid.NewGuid(),
            projectId,
            speakerId,
            DateTimeOffset.UtcNow,
            CurrentVersion,
            isThirdPartyConsent,
            notes,
            ExpiresAtUtc: null,
            RevokedAtUtc: null);
}
