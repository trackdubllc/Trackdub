using Trackdub.Domain.Speakers;

namespace Trackdub.Contracts;

/// <summary>
/// Durable per-speaker voice-clone consent persisted with the project.
/// Session opt-in is separate and is handled by IConsentService.
/// </summary>
public interface ISpeakerConsentService
{
    Task<bool> IsConsentGrantedAsync(Guid speakerId, CancellationToken cancellationToken);

    Task<VoiceCloneConsentRecord?> GetConsentAsync(Guid speakerId, CancellationToken cancellationToken);

    Task<VoiceCloneConsentRecord> RecordConsentAsync(
        Guid projectId,
        Guid speakerId,
        bool isThirdPartyConsent,
        string? notes,
        CancellationToken cancellationToken);

    Task RevokeConsentAsync(Guid speakerId, CancellationToken cancellationToken);
}
