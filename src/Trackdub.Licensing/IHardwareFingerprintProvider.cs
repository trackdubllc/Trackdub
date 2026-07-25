namespace Trackdub.Licensing;

/// <summary>
/// Platform-specific hardware fingerprint generation.
/// </summary>
public interface IHardwareFingerprintProvider
{
    string GetFingerprint();
}
