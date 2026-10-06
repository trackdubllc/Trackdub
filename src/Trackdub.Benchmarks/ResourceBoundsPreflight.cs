using Microsoft.Extensions.DependencyInjection;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Benchmarks;

/// <summary>
/// Rejects resource-telemetry bounds that no run could ever satisfy on the host it is about to run
/// on, before any iteration is measured.
/// </summary>
/// <remarks>
/// <para>
/// The free-VRAM floor is the only bound that can be physically impossible on its own:
/// <c>--min-available-vram-mb</c> is a <em>minimum</em> on adapter-wide headroom, so a floor above
/// the video memory telemetry can ever report cannot be met by any run on this host, whatever the
/// workload. Finding that out from a failed measurement after a full benchmark wastes the whole
/// run, and recording the bound in evidence implies the hardware could have met it.
/// </para>
/// <para>
/// The ceiling compared against must be the <em>same</em> reading the run's own validation
/// enforces: <c>IAvailableVramReader</c> samples the DXGI LOCAL segment (≈ the adapter's dedicated
/// video memory) of the reader's device index (0 by default). Projecting the largest adapter's
/// dedicated-plus-shared total instead would pass floors that live telemetry can never satisfy —
/// shared memory belongs to the SYSTEM segment and other adapters are never sampled. An unknown
/// capacity — no GPU adapter at the telemetry index, a host that cannot enumerate devices, a
/// failed enumeration — skips the check rather than guessing, so the pre-flight only ever rejects
/// a bound it can prove impossible.
/// </para>
/// <para>
/// The remaining bounds are maxima. A ceiling above the host's capacity is vacuous rather than
/// impossible — every reading satisfies it — so it stays accepted, matching the documented
/// "omitted limits are unbounded" contract.
/// </para>
/// </remarks>
internal static class ResourceBoundsPreflight
{
    /// <summary>
    /// The device index the run's free-VRAM telemetry samples: the default device index of
    /// <c>WindowsAvailableVramReader</c>, the reader registered for <c>IAvailableVramReader</c>.
    /// </summary>
    internal const int TelemetryDeviceIndex = 0;

    /// <summary>
    /// The GPU adapters of an enumeration — the only devices that report adapter video memory —
    /// shared by the pre-flight and the capacity banner so their device filter cannot drift.
    /// </summary>
    internal static IEnumerable<DeviceEntry> GpuAdapters(IEnumerable<DeviceEntry> devices) =>
        devices.Where(device => device.Kind is DeviceKind.DiscreteGpu or DeviceKind.IntegratedGpu);

    /// <summary>
    /// One adapter's full reported memory (dedicated plus shared) — the diagnostic aggregate the
    /// banner displays, not a feasibility input: shared memory is outside the LOCAL segment
    /// telemetry samples.
    /// </summary>
    internal static long TotalAdapterMemoryMb(DeviceEntry device) =>
        (long)device.DedicatedVramMb + device.SharedMemoryMb;

    /// <summary>
    /// One adapter's video memory in the segment free-VRAM telemetry measures: the DXGI LOCAL
    /// segment corresponds to the adapter's dedicated video memory.
    /// </summary>
    internal static long TelemetrySegmentVideoMemoryMb(DeviceEntry device) => device.DedicatedVramMb;

    /// <summary>
    /// The video memory, in MB, a <c>--min-available-vram-mb</c> floor is checked against: the
    /// dedicated (LOCAL-segment) memory of the GPU adapter at <see cref="TelemetryDeviceIndex"/> —
    /// exactly what the run's own free-VRAM reader can report. Returns 0 when the host cannot
    /// report such an adapter, which callers read as "capacity unknown".
    /// </summary>
    internal static async Task<long> QueryTotalVideoMemoryMbAsync(
        IServiceProvider? hostServices,
        CancellationToken cancellationToken)
    {
        IDeviceEnumerator? enumerator = hostServices?.GetService<IDeviceEnumerator>();
        if (enumerator is null)
        {
            return 0;
        }

        try
        {
            IReadOnlyList<DeviceEntry> devices = await enumerator
                .GetDevicesAsync(cancellationToken)
                .ConfigureAwait(false);

            return EffectiveVideoMemoryMb(devices);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Adapter discovery answers "how much could this host report", not the requested
            // measurement: a host that cannot answer leaves the capacity unknown instead of
            // turning the pre-flight into a failure.
            return 0;
        }
    }

    /// <summary>
    /// The effective video-memory capacity, in MB, that a per-adapter bound is measured against:
    /// the telemetered GPU adapter's dedicated (LOCAL-segment) memory — the reading the run's own
    /// free-VRAM validation enforces. Returns 0 when the host reports no such adapter, which
    /// callers read as "capacity unknown".
    /// </summary>
    internal static long EffectiveVideoMemoryMb(IReadOnlyList<DeviceEntry> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        // CPU entries report no video memory, and an NPU's working set is a device-local estimate
        // rather than the adapter memory `availableVramMb` measures, so only GPU adapters at the
        // telemetry index are candidates.
        DeviceEntry? telemetryAdapter = GpuAdapters(devices)
            .FirstOrDefault(device => device.DeviceIndex == TelemetryDeviceIndex);

        return telemetryAdapter is null ? 0 : TelemetrySegmentVideoMemoryMb(telemetryAdapter);
    }

    /// <summary>
    /// Describes the first physically impossible bound in <paramref name="bounds"/> for a host whose
    /// telemetered adapter can address <paramref name="totalVideoMemoryMb"/> MB, or
    /// <see langword="null"/> when every configured bound is satisfiable or the capacity is unknown.
    /// </summary>
    internal static string? DescribeImpossibleBound(ResourceTelemetryBounds bounds, long totalVideoMemoryMb)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        if (totalVideoMemoryMb <= 0 ||
            bounds.MinAvailableVramMb is not long floor ||
            floor <= totalVideoMemoryMb)
        {
            return null;
        }

        return $"--min-available-vram-mb {floor} exceeds the {totalVideoMemoryMb} MB of video memory "
            + "free-VRAM telemetry can ever report on this host (the telemetered adapter's dedicated "
            + "memory), so no run could leave that much VRAM headroom. "
            + "Lower the floor, or run the benchmark on an adapter with more memory.";
    }
}
