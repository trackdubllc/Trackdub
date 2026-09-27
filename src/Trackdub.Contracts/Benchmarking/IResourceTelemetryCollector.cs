using Trackdub.Domain.Benchmarking;

namespace Trackdub.Contracts.Benchmarking;

public interface IResourceTelemetryCollector
{
    ResourceUsageSnapshot Capture();
}
