using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.TestDoubles;

public sealed class FakeDeviceEnumerator : IDeviceEnumerator
{
    public IReadOnlyList<DeviceEntry> Devices { get; set; } = [];

    /// <summary>
    /// Optional separate list returned by <see cref="ReEnumerateAsync"/>.
    /// When null, <see cref="ReEnumerateAsync"/> returns <see cref="Devices"/>.
    /// </summary>
    public IReadOnlyList<DeviceEntry>? ReEnumeratedDevices { get; set; }

    public FakeDeviceEnumerator() { }

    public FakeDeviceEnumerator(IReadOnlyList<DeviceEntry> devices)
    {
        Devices = devices;
    }

    public Task<IReadOnlyList<DeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(Devices);

    public Task<IReadOnlyList<DeviceEntry>> ReEnumerateAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(ReEnumeratedDevices ?? Devices);
}
