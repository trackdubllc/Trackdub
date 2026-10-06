using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trackdub.Inference.Onnx.Runtime;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Composition;

/// <summary>
/// Creates the platform device enumerator that <see cref="CompositionRoot.AddTrackdub"/> registers,
/// for callers that must describe the host's adapters and video memory before a container exists
/// (for example the benchmark CLI's host-capacity banner).
/// </summary>
/// <remarks>
/// This is the single owner of the platform dispatch; the <c>IDeviceEnumerator</c> registration in
/// <see cref="CompositionRoot"/> delegates here. With no OpenVINO runtime to probe, NPU entries that depend on it
/// are omitted; GPU adapters — the only devices that report video memory — enumerate identically.
/// The returned enumerator caches its device list for the process lifetime, so a caller that needs
/// a fresh reading must use <see cref="IDeviceEnumerator.ReEnumerateAsync"/> or create a new
/// instance.
/// </remarks>
public static class DeviceEnumeratorFactory
{
    public static IDeviceEnumerator Create(
        IOpenVinoAvailabilityProvider? openVinoAvailability = null,
        ILoggerFactory? loggerFactory = null)
    {
        openVinoAvailability ??= new NullOpenVinoAvailabilityProvider();
        loggerFactory ??= NullLoggerFactory.Instance;

#if WINDOWS
        return new WindowsDeviceEnumerator(
            openVinoAvailability,
            loggerFactory.CreateLogger<WindowsDeviceEnumerator>());
#elif MACOS
#pragma warning disable CA1416 // macOS-only type constructed under the MACOS compile constant
        return new MacDeviceEnumerator(loggerFactory.CreateLogger<MacDeviceEnumerator>());
#pragma warning restore CA1416
#elif LINUX
#pragma warning disable CA1416 // Linux-only type constructed under the LINUX compile constant
        return CreateLinux(openVinoAvailability, loggerFactory, new PhysicalSysfsReader());
#pragma warning restore CA1416
#else
        // Portable TFM with no platform define: mirror the composition root's CPU-only fallback so
        // callers see the same (empty) adapter list the run itself would.
        return new CpuOnlyDeviceEnumerator();
#endif
    }

#if LINUX
    /// <summary>
    /// Linux enumerator over an explicit sysfs reader, so the composition root can hand in the
    /// reader it registered. <c>ISysfsReader</c> is only compiled into Linux builds, hence the
    /// guard instead of an optional parameter on <see cref="Create"/>.
    /// </summary>
#pragma warning disable CA1416 // Linux-only type constructed under the LINUX compile constant
    internal static IDeviceEnumerator CreateLinux(
        IOpenVinoAvailabilityProvider openVinoAvailability,
        ILoggerFactory loggerFactory,
        ISysfsReader sysfsReader) =>
        new LinuxDeviceEnumerator(
            openVinoAvailability,
            sysfsReader,
            loggerFactory.CreateLogger<LinuxDeviceEnumerator>());
#pragma warning restore CA1416
#endif
}
