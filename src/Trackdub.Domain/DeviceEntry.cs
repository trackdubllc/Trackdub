namespace Trackdub.Domain;

/// <summary>
/// Immutable record representing a single compute device with its capabilities.
/// </summary>
public sealed record DeviceEntry(
    DeviceKind Kind,
    int DeviceIndex,
    string AdapterDescription,
    string VendorName,
    int DedicatedVramMb,
    int SharedMemoryMb,
    IReadOnlyList<ExecutionProviderKind> SupportedProviders,
    long? AdapterLuid = null);
