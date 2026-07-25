namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Provides information about OpenVINO runtime availability and configuration.
/// Used by <see cref="IDeviceEnumerator"/> to determine whether NPU device entries
/// should be included in the enumerated device list and whether CPU proxy mode is active.
/// </summary>
public interface IOpenVinoAvailabilityProvider
{
    /// <summary>
    /// Gets whether the OpenVINO runtime is installed and its native libraries loaded successfully.
    /// </summary>
    bool IsAvailable { get; }

    /// <summary>
    /// Gets whether CPU proxy mode is active (device type "CPU" instead of "NPU").
    /// When true, downstream provider routing should be able to reach OpenVINO via CPU execution.
    /// </summary>
    bool UseOpenVinoCpuProxy { get; }
}
