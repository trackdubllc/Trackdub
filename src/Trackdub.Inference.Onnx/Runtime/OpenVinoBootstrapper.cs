using System.Runtime.InteropServices;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.Extensions.Logging;

namespace Trackdub.Inference.Onnx.Runtime;

/// <summary>
/// Manages the lifecycle of OpenVINO native library loading.
/// Checks the component store for an installed OpenVINO package, dynamically loads
/// the native library, and reports availability to the device enumerator and EP discovery.
/// </summary>
public sealed class OpenVinoBootstrapper : IOpenVinoAvailabilityProvider, IDisposable
{
    /// <summary>
    /// Component identifier used to look up OpenVINO in the component store.
    /// </summary>
    internal const string ComponentId = "openvino";

    private static readonly string NativeLibraryName = OperatingSystem.IsLinux()
        ? "libopenvino_c.so"
        : "openvino_c.dll";

    private static readonly string FallbackNativeLibraryName = OperatingSystem.IsLinux()
        ? "libopenvino.so.2025.0"
        : "openvino.dll";

    private readonly ILogger<OpenVinoBootstrapper> _logger;
    private readonly nint _libraryHandle;
    private bool _disposed;

    /// <summary>
    /// Gets whether the OpenVINO runtime is installed and its native libraries loaded successfully.
    /// </summary>
    public bool IsAvailable { get; }

    /// <summary>
    /// Gets whether CPU proxy mode is active (device type "CPU" instead of "NPU").
    /// </summary>
    public bool UseOpenVinoCpuProxy { get; }

    public OpenVinoBootstrapper(
        Func<string, bool> isComponentInstalled,
        Func<string, string?> getComponentInstallPath,
        bool useOpenVinoCpuProxy,
        ILogger<OpenVinoBootstrapper> logger)
    {
        ArgumentNullException.ThrowIfNull(isComponentInstalled);
        ArgumentNullException.ThrowIfNull(getComponentInstallPath);
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        UseOpenVinoCpuProxy = useOpenVinoCpuProxy;

        (IsAvailable, _libraryHandle) = TryBootstrap(isComponentInstalled, getComponentInstallPath);
    }

    /// <summary>
    /// Returns the device type string for OpenVINO EP session configuration.
    /// Returns "CPU" when <see cref="UseOpenVinoCpuProxy"/> is true, otherwise "NPU".
    /// </summary>
    public string DeviceTypeString => UseOpenVinoCpuProxy ? "CPU" : "NPU";

    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    ~OpenVinoBootstrapper()
    {
        Dispose(disposing: false);
    }

    private void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        if (_libraryHandle != nint.Zero)
        {
            try
            {
                NativeLibrary.Free(_libraryHandle);
                _logger.LogDebug("OpenVINO native library handle released.");
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to release OpenVINO native library handle.");
            }
        }

        _disposed = true;
    }

    private (bool Available, nint Handle) TryBootstrap(
        Func<string, bool> isComponentInstalled,
        Func<string, string?> getComponentInstallPath)
    {
        if (!isComponentInstalled(ComponentId))
        {
            _logger.LogDebug("OpenVINO component is not installed in the component store.");
            return (false, nint.Zero);
        }

        string? installPath = getComponentInstallPath(ComponentId);
        if (string.IsNullOrWhiteSpace(installPath))
        {
            _logger.LogWarning(
                "OpenVINO component is marked as installed but no install path was returned.");
            return (false, nint.Zero);
        }

        return TryLoadNativeLibrary(installPath);
    }

    private (bool Available, nint Handle) TryLoadNativeLibrary(string installPath)
    {
        // Try primary library first, then fallback
        string primaryPath = Path.Combine(installPath, NativeLibraryName);
        string fallbackPath = Path.Combine(installPath, FallbackNativeLibraryName);

        if (TryLoadFromPath(primaryPath, out nint handle))
        {
            _logger.LogInformation(
                "OpenVINO native library loaded successfully from '{Path}'.", primaryPath);
            return (true, handle);
        }

        if (TryLoadFromPath(fallbackPath, out handle))
        {
            _logger.LogInformation(
                "OpenVINO native library loaded successfully from '{Path}'.", fallbackPath);
            return (true, handle);
        }

        _logger.LogWarning(
            "Failed to load OpenVINO native library. Tried '{Primary}' and '{Fallback}'. " +
            "OpenVINO will be treated as not installed for this session.",
            primaryPath,
            fallbackPath);
        return (false, nint.Zero);
    }

    private bool TryLoadFromPath(string libraryPath, out nint handle)
    {
        handle = nint.Zero;

        if (!File.Exists(libraryPath))
        {
            _logger.LogDebug("OpenVINO library not found at '{Path}'.", libraryPath);
            return false;
        }

        try
        {
            handle = NativeLibrary.Load(libraryPath);
            return handle != nint.Zero;
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex, "OpenVINO DLL not found at '{Path}'.", libraryPath);
            return false;
        }
        catch (BadImageFormatException ex)
        {
            _logger.LogWarning(ex, "OpenVINO library at '{Path}' has incompatible architecture.", libraryPath);
            return false;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Unexpected error loading OpenVINO library from '{Path}'.", libraryPath);
            return false;
        }
    }
}
