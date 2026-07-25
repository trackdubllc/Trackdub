namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Session-scoped voice-cloning consent gate.
/// This is not durable project or speaker consent; persisted voice-clone consent is handled by ISpeakerConsentService.
/// </summary>
public interface IConsentService
{
    /// <summary>
    /// Identifies the current application session for audit and UI messaging.
    /// </summary>
    Guid SessionId { get; }

    /// <summary>
    /// True only after the user has granted voice-cloning consent for this application session.
    /// </summary>
    bool IsVoiceCloningConsentGranted { get; }

    event EventHandler? VoiceCloningConsentChanged;

    void GrantVoiceCloningConsent();

    void ClearVoiceCloningConsent();
}

public sealed class ConsentRequiredException : InvalidOperationException
{
    public ConsentRequiredException()
        : base("Voice cloning requires explicit consent for this application session.")
    {
    }

    public ConsentRequiredException(string message)
        : base(message)
    {
    }
}

public sealed class TtsReferenceTextRequiredException : InvalidOperationException
{
    public TtsReferenceTextRequiredException()
        : base("Qwen3-TTS Base voice cloning requires reference transcript text for ICL mode.")
    {
    }

    public TtsReferenceTextRequiredException(string message)
        : base(message)
    {
    }
}
