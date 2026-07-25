using Trackdub.Contracts;

namespace Trackdub.TestDoubles;

public sealed class FakeAuditLog : IVoiceCloneAuditLog
{
    private readonly List<VoiceCloneAuditEntry> entries = [];

    public IReadOnlyList<VoiceCloneAuditEntry> Entries => entries;

    public bool TreatAsTampered { get; set; }

    public Task AppendAsync(VoiceCloneAuditEntry entry, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (TreatAsTampered)
        {
            throw new InvalidOperationException("Fake audit log integrity verification failed.");
        }

        entries.Add(entry);
        return Task.CompletedTask;
    }

    public Task<VoiceCloneAuditVerificationResult> VerifyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(
            TreatAsTampered
                ? new VoiceCloneAuditVerificationResult(false, entries.Count, "Fake audit log was tampered.")
                : new VoiceCloneAuditVerificationResult(true, entries.Count));
    }
}
