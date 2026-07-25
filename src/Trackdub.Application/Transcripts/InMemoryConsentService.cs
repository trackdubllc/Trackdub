using Trackdub.Contracts.Pipeline;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// In-memory implementation of the session voice-cloning consent gate.
/// </summary>
public sealed class InMemoryConsentService : IConsentService
{
    private readonly object gate = new();
    private bool isVoiceCloningConsentGranted;

    public Guid SessionId { get; } = Guid.NewGuid();

    public bool IsVoiceCloningConsentGranted
    {
        get
        {
            lock (gate)
            {
                return isVoiceCloningConsentGranted;
            }
        }
    }

    public event EventHandler? VoiceCloningConsentChanged;

    public void GrantVoiceCloningConsent()
    {
        bool changed;
        lock (gate)
        {
            changed = !isVoiceCloningConsentGranted;
            isVoiceCloningConsentGranted = true;
        }

        if (changed)
        {
            VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearVoiceCloningConsent()
    {
        bool changed;
        lock (gate)
        {
            changed = isVoiceCloningConsentGranted;
            isVoiceCloningConsentGranted = false;
        }

        if (changed)
        {
            VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
        }
    }
}
