namespace Trackdub.Contracts.Pipeline;

/// <summary>
/// Where a TTS run is planned to execute, so the stage can bound synthesis concurrency by the
/// memory of the device that runs it rather than the largest adapter on the host.
/// </summary>
/// <param name="AcceleratorRouted">False when the plan selects a CPU or DNNL provider.</param>
/// <param name="DeviceVramMb">
/// Dedicated memory in MB of the planned device, or the smallest GPU when the planned device is
/// unknown. Null when no usable memory reading exists.
/// </param>
public sealed record TtsAcceleratorPlacement(bool AcceleratorRouted, long? DeviceVramMb);

public interface ITtsAcceleratorPlacementResolver
{
    /// <summary>
    /// Plans TTS for <paramref name="options"/> the same way synthesis does and reports the
    /// selected placement. Returns null when no runnable plan exists.
    /// </summary>
    Task<TtsAcceleratorPlacement?> ResolvePlacementAsync(
        InferenceRequestOptions options,
        string? languageCode,
        CancellationToken cancellationToken);
}
