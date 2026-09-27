namespace Trackdub.Domain.Benchmarking;

public sealed record ResourceTelemetryValidation
{
    public ResourceTelemetryStatus Status { get; init; }
    public double? CpuTimeMilliseconds { get; init; }
    public double? ElapsedMilliseconds { get; init; }
    public int? ProcessorCount { get; init; }
    public IReadOnlyList<ResourceTelemetryCheck> Checks { get; init; } = [];
}
