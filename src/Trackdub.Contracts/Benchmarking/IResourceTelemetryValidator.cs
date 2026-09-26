using Trackdub.Domain.Benchmarking;

namespace Trackdub.Contracts.Benchmarking;

public interface IResourceTelemetryValidator
{
    ResourceTelemetryValidation Validate(
        ResourceUsageSnapshot? start,
        ResourceUsageSnapshot? end,
        ResourceTelemetryBounds bounds);
}
