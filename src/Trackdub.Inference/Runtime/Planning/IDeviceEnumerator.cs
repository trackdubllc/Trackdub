using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

public interface IDeviceEnumerator
{
    /// <summary>
    /// Returns the cached list of discovered devices, ordered by kind priority
    /// (dGPU first, iGPU, NPU, CPU last) with ties broken by device index ascending.
    /// </summary>
    Task<IReadOnlyList<DeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Forces re-enumeration of devices without invalidating in-flight sessions.
    /// Returns the fresh device list.
    /// </summary>
    Task<IReadOnlyList<DeviceEntry>> ReEnumerateAsync(CancellationToken cancellationToken = default);
}
