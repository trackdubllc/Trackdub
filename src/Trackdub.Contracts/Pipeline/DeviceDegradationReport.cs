namespace Trackdub.Contracts.Pipeline;

public enum DeviceDegradationKind
{
    MemoryExhausted,
    DeviceFailed
}

public sealed record DeviceDegradationReport(
    DeviceDegradationKind Kind,
    int FailedDeviceIndex,
    string FailedAdapterDescription,
    string ErrorDetail,
    int? FallbackDeviceIndex = null,
    string? FallbackAdapterDescription = null);

public interface IDeviceDegradationReporter
{
    DeviceDegradationReport? LastDeviceDegradation { get; }
}
