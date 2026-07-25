namespace Trackdub.TestDoubles;

/// <summary>
/// Test double for OpenVinoBootstrapper with configurable availability and device type.
/// </summary>
public sealed class FakeOpenVinoBootstrapper
{
    /// <summary>
    /// Gets or sets whether OpenVINO is available (installed and loaded).
    /// </summary>
    public bool IsAvailable { get; set; }

    /// <summary>
    /// Gets or sets the device type string returned by this bootstrapper ("NPU" or "CPU").
    /// </summary>
    public string DeviceTypeString { get; set; } = "NPU";
}
