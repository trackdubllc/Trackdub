namespace Trackdub.Contracts;

/// <summary>
/// Discoverability and honesty markers for the NVIDIA Audio Effects (AFX) integration.
/// While <see cref="IsStubbed"/> is true, readiness must never report Ready and downloads
/// must refuse to install packages (placeholder or otherwise).
/// </summary>
public static class NvidiaAfxIntegration
{
    public const string ProviderId = "nvidia-afx";

    public const string DisplayName = "NVIDIA Audio Effects (AFX)";

    public const string StubStatusLabel = "Stub";

    public const string StubReason =
        "NVIDIA AFX remains stubbed for readiness: Trackdub-hosted Maxine 3.x redistributable URLs/checksums " +
        "are not published yet, and NVAudioEffects.dll create/run has not been verified on shipping GPUs. " +
        "Native API bindings, Maxine features/ layout resolution, settings→stage options, and packaging gates " +
        "are in place; install NVIDIA Maxine AFX (core + NGC features) or set TRACKDUB_NVIDIA_AFX_RUNTIME_ROOT " +
        "for local probing only after IsStubbed() flips.";

    public const string LicenseUrl =
        "https://www.nvidia.com/en-us/agreements/enterprise-software/nvidia-software-license-agreement/";

    public const string DeveloperResourcesUrl = "https://www.nvidia.com/broadcast-sdk-resources";

    // Method (not const) so stub gates do not create CS0162 unreachable-code failures while
    // the real install/readiness bodies remain compiled for the future live flip.
    public static bool IsStubbed() => true;
}
