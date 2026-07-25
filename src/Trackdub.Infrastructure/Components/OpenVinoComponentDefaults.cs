namespace Trackdub.Infrastructure.Components;

/// <summary>
/// Versioned defaults for the downloadable OpenVINO runtime package.
/// Environment variables can override these values at runtime.
/// </summary>
public static class OpenVinoComponentDefaults
{
    public const string PackageVersion = "openvino-2026.05.0";
    public const string DownloadUrl = "https://github.com/tonythethompson/Trackdub/releases/download/openvino/openvino-runtime.zip";
    public const string ExpectedSha256Hash = "fcbcbf8f8a7fd2dca739f6f7604b250672ab53a7f7ca6fc015b8140f7190f72d";
    public const long ExpectedFileSizeBytes = 272629760;
}
