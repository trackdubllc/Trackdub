using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeConsentService : IConsentService
{
    public Guid SessionId { get; set; } = Guid.NewGuid();

    public bool IsVoiceCloningConsentGranted { get; private set; }

    public event EventHandler? VoiceCloningConsentChanged;

    public void GrantVoiceCloningConsent()
    {
        IsVoiceCloningConsentGranted = true;
        VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
    }

    public void ClearVoiceCloningConsent()
    {
        IsVoiceCloningConsentGranted = false;
        VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
    }
}
