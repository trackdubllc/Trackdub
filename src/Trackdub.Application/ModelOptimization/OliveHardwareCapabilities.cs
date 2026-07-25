namespace Trackdub.Application.ModelOptimization;

/// <summary>Detected hardware capabilities relevant to Olive EP selection.</summary>
public sealed record OliveHardwareCapabilities(
    bool HasNvidiaGpu,
    bool HasAnyGpu)
{
    public static OliveHardwareCapabilities Unknown { get; } = new(false, false);
}
