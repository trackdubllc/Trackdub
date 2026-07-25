namespace Trackdub.Licensing;

/// <summary>
/// Thrown when hardware fingerprint generation fails on any platform.
/// Wraps platform-specific exceptions into a single type for the orchestrator to catch.
/// </summary>
public sealed class FingerprintException : Exception
{
    public FingerprintException(string message)
        : base(message)
    {
    }

    public FingerprintException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
