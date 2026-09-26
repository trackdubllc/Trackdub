namespace Trackdub.Domain.Benchmarking;

/// <summary>
/// One metric's verdict. <see cref="Threshold"/> is the configured bound in the metric's own
/// direction: an inclusive maximum for usage metrics, an inclusive minimum for the free-VRAM floor.
/// </summary>
public sealed record ResourceTelemetryCheck(
    string Metric,
    ResourceTelemetryStatus Status,
    double? ObservedValue,
    double? Threshold,
    string? Reason);
