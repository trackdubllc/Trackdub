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
/// the video memory the adapters can address cannot be met by any run on this host, whatever the
/// workload. Finding that out from a failed measurement after a full benchmark wastes the whole
/// run, and recording the bound in evidence implies the hardware could have met it.
/// </para>
/// <para>
/// The ceiling compared against is per adapter and covers dedicated plus shared video memory: on
/// WDDM an adapter's budget includes a share of system memory, so a floor above dedicated VRAM
/// alone is still satisfiable and must not be rejected. An unknown capacity — no GPU adapter, a
/// host that cannot enumerate devices, a failed enumeration — skips the check rather than
/// guessing, so the pre-flight only ever rejects a bound it can prove impossible.
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
    /// Total addressable video memory, in MB, of the host's most capable GPU adapter: its
    /// dedicated plus shared memory. Returns 0 when the host cannot report a GPU adapter, which
    /// callers read as "capacity unknown".
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
    /// the largest GPU adapter's dedicated plus shared memory. Returns 0 when no GPU adapter
    /// reports memory, which callers read as "capacity unknown".
    /// </summary>
    internal static long EffectiveVideoMemoryMb(IReadOnlyList<DeviceEntry> devices)
    {
        ArgumentNullException.ThrowIfNull(devices);

        long capacity = 0;
        foreach (DeviceEntry device in devices)
        {
            // CPU entries report no video memory, and an NPU's working set is a device-local
            // estimate rather than the adapter memory `availableVramMb` measures.
            if (device.Kind is not (DeviceKind.DiscreteGpu or DeviceKind.IntegratedGpu))
            {
                continue;
            }

            capacity = Math.Max(capacity, (long)device.DedicatedVramMb + device.SharedMemoryMb);
        }

        return capacity;
    }

    /// <summary>
    /// Describes the first physically impossible bound in <paramref name="bounds"/> for a host whose
    /// most capable adapter can address <paramref name="totalVideoMemoryMb"/> MB, or
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

        return $"--min-available-vram-mb {floor} exceeds this host's total video memory of "
            + $"{totalVideoMemoryMb} MB, so no run could leave that much VRAM headroom. "
            + "Lower the floor, or run the benchmark on an adapter with more memory.";
    }
}
