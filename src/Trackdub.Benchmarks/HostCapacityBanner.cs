using Trackdub.Composition;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;

namespace Trackdub.Benchmarks;

/// <summary>
/// Prints the host's detected devices, its video memory, and the effective capacity a resource
/// bound is measured against, so a bound's feasibility is visible before a run starts.
/// </summary>
/// <remarks>
/// <para>
/// The reported capacity comes from the same device enumeration the run's own pre-flight uses
/// (<see cref="ResourceBoundsPreflight"/>), so the banner and the pre-flight cannot disagree about
/// whether a floor is reachable. The banner is diagnostic only: an enumeration failure leaves the
/// capacity unknown and never changes the run's outcome, exactly like the pre-flight.
/// </para>
/// <para>
/// Device enumeration here has no OpenVINO runtime to probe, so NPU entries that depend on it are
/// absent; GPU adapters — the only devices that report video memory — are enumerated identically.
/// </para>
/// </remarks>
internal static class HostCapacityBanner
{
    internal static async Task WriteAsync(
        TextWriter output,
        ResourceTelemetryBounds bounds,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(output);
        ArgumentNullException.ThrowIfNull(bounds);

        IReadOnlyList<DeviceEntry>? devices = null;
        try
        {
            devices = await DeviceEnumeratorFactory.Create()
                .GetDevicesAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // Diagnostic only: the run's pre-flight treats an unenumerable host as an unknown
            // capacity, and this banner must not turn that into a failure.
        }

        foreach (string line in Describe(devices, bounds))
        {
            output.WriteLine(line);
        }
    }

    /// <summary>
    /// The lines the banner prints for <paramref name="devices"/> — <see langword="null"/> when the
    /// host could not be enumerated — under <paramref name="bounds"/>.
    /// </summary>
    internal static IReadOnlyList<string> Describe(
        IReadOnlyList<DeviceEntry>? devices,
        ResourceTelemetryBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(bounds);

        var lines = new List<string> { "Host capacity" };
        if (devices is null)
        {
            lines.Add("  Devices: unavailable - this host and build cannot enumerate compute devices.");
            lines.Add("  Effective VRAM capacity: unknown.");
            lines.Add(DescribeBoundFeasibility(bounds, capacityMb: 0));
            return lines;
        }

        if (devices.Count == 0)
        {
            lines.Add("  Devices: none detected.");
        }
        else
        {
            foreach (DeviceEntry device in devices)
            {
                lines.Add($"  {DescribeDevice(device)}");
            }
        }

        long detectedVideoMemoryMb = 0;
        int gpuAdapterCount = 0;
        foreach (DeviceEntry device in devices)
        {
            if (device.Kind is not (DeviceKind.DiscreteGpu or DeviceKind.IntegratedGpu))
            {
                continue;
            }

            detectedVideoMemoryMb += (long)device.DedicatedVramMb + device.SharedMemoryMb;
            gpuAdapterCount++;
        }

        if (gpuAdapterCount > 0)
        {
            string adapters = gpuAdapterCount == 1 ? "1 GPU adapter" : $"{gpuAdapterCount} GPU adapters";
            lines.Add($"  Detected video memory: {detectedVideoMemoryMb} MB across {adapters}.");
        }

        long capacityMb = ResourceBoundsPreflight.EffectiveVideoMemoryMb(devices);
        lines.Add(capacityMb > 0
            ? $"  Effective VRAM capacity: {capacityMb} MB - the sampled adapter's local-segment "
                + "memory, which --min-available-vram-mb is checked against."
            : "  Effective VRAM capacity: unknown - no GPU adapter reported memory.");

        lines.Add(DescribeBoundFeasibility(bounds, capacityMb));
        return lines;
    }

    private static string DescribeDevice(DeviceEntry device)
    {
        string kind = device.Kind switch
        {
            DeviceKind.DiscreteGpu => "discrete GPU",
            DeviceKind.IntegratedGpu => "integrated GPU",
            DeviceKind.Npu => "NPU",
            _ => "CPU",
        };
        string identity = $"{kind} #{device.DeviceIndex}: {device.AdapterDescription} ({device.VendorName})";
        string memory = device.Kind is DeviceKind.Cpu
            ? "no adapter memory"
            : $"{device.DedicatedVramMb} MB dedicated + {device.SharedMemoryMb} MB shared";
        string providers = device.SupportedProviders.Count == 0
            ? string.Empty
            : $" [providers: {string.Join(", ", device.SupportedProviders)}]";

        return $"{identity} - {memory}{providers}";
    }

    private static string DescribeBoundFeasibility(ResourceTelemetryBounds bounds, long capacityMb)
    {
        if (bounds.MinAvailableVramMb is not long floor)
        {
            return "  Bound feasibility: no --min-available-vram-mb floor configured.";
        }

        if (capacityMb <= 0)
        {
            return $"  Bound feasibility: --min-available-vram-mb {floor} MB cannot be checked "
                + "because the host's video memory is unknown.";
        }

        return ResourceBoundsPreflight.DescribeImpossibleBound(bounds, capacityMb) is string impossible
            ? $"  Bound feasibility: {impossible}"
            : $"  Bound feasibility: --min-available-vram-mb {floor} MB is within the host's "
                + "effective capacity.";
    }
}
