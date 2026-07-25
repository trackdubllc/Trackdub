namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Queries live available VRAM for known compute devices.
/// </summary>
public interface IVramMonitor
{
    /// <summary>
    /// Returns the available VRAM in MB for the device at <paramref name="deviceIndex"/>
    /// in the current device list, or null if the query is not supported on this platform
    /// or for this device.
    /// </summary>
    long? QueryAvailableVramMb(int deviceIndex);
}
