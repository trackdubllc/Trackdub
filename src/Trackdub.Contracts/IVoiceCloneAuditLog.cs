namespace Trackdub.Contracts;

public interface IVoiceCloneAuditLog
{
    Task AppendAsync(VoiceCloneAuditEntry entry, CancellationToken cancellationToken);

    Task<VoiceCloneAuditVerificationResult> VerifyAsync(CancellationToken cancellationToken);
}

public sealed record VoiceCloneAuditEntry(
    DateTimeOffset TimestampUtc,
    Guid SessionId,
    Guid SpeakerId,
    Guid ReferenceClipArtifactId);

public sealed record VoiceCloneAuditVerificationResult(
    bool IsValid,
    int EntryCount,
    string? FailureReason = null);
