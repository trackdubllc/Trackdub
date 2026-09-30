namespace Trackdub.Contracts;

/// <summary>
/// Discoverability and honesty markers for the NVIDIA Audio Effects (AFX) integration.
/// While <see cref="IsStubbed"/> is true, readiness must never report Ready and downloads
/// must refuse to install placeholder packages.
/// </summary>
public static class NvidiaAfxIntegration
{
    public const string ProviderId = "nvidia-afx";

    public const string DisplayName = "NVIDIA Audio Effects (AFX)";

    public const string StubStatusLabel = "Stub";

    public const string StubReason =
        "NVIDIA AFX is registered as a stub. Native NvAudioEffects runtime packaging and download URLs are not wired yet.";

    // Method (not const) so stub gates do not create CS0162 unreachable-code failures while
    // the real install/readiness bodies remain compiled for the future live flip.
    public static bool IsStubbed() => true;
}
