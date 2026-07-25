using Trackdub.Contracts;
using Trackdub.Domain.Speakers;

namespace Trackdub.TestDoubles;

public sealed class FakeSpeakerConsentService : ISpeakerConsentService
{
    private readonly Dictionary<Guid, VoiceCloneConsentRecord> records = new();

    public Task<bool> IsConsentGrantedAsync(Guid speakerId, CancellationToken cancellationToken)
    {
        bool granted = records.TryGetValue(speakerId, out VoiceCloneConsentRecord? record) && record.IsActive;
        return Task.FromResult(granted);
    }

    public Task<VoiceCloneConsentRecord?> GetConsentAsync(Guid speakerId, CancellationToken cancellationToken)
    {
        records.TryGetValue(speakerId, out VoiceCloneConsentRecord? record);
        return Task.FromResult(record);
    }

    public Task<VoiceCloneConsentRecord> RecordConsentAsync(
        Guid projectId,
        Guid speakerId,
        bool isThirdPartyConsent,
        string? notes,
        CancellationToken cancellationToken)
    {
        VoiceCloneConsentRecord record = VoiceCloneConsentRecord.Create(projectId, speakerId, isThirdPartyConsent, notes);
        records[speakerId] = record;
        return Task.FromResult(record);
    }

    public Task RevokeConsentAsync(Guid speakerId, CancellationToken cancellationToken)
    {
        if (records.TryGetValue(speakerId, out VoiceCloneConsentRecord? record))
        {
            records[speakerId] = record with { RevokedAtUtc = DateTimeOffset.UtcNow };
        }

        return Task.CompletedTask;
    }

    public void GrantConsent(Guid projectId, Guid speakerId) =>
        records[speakerId] = VoiceCloneConsentRecord.Create(projectId, speakerId, isThirdPartyConsent: false, notes: null);
}
