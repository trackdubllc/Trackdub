using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using Microsoft.Extensions.Logging;

namespace Trackdub.Inference.Onnx.Runtime;

/// <summary>
/// Enumerates available compute devices using DXGI adapter enumeration for GPUs,
/// always includes a CPU entry, and conditionally includes an NPU entry when
/// an Intel CPU is detected and OpenVINO is installed.
/// Results are cached for the process lifetime; <see cref="ReEnumerateAsync"/> refreshes atomically.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsDeviceEnumerator : IDeviceEnumerator
{
    private readonly IOpenVinoAvailabilityProvider _openVinoAvailability;
    private readonly Func<bool> _hasIntelNpuPciDevice;
    private readonly ILogger<WindowsDeviceEnumerator> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private volatile IReadOnlyList<DeviceEntry>? _cachedDevices;

    /// <summary>
    /// Minimum dedicated VRAM (in MB) to classify an adapter as discrete
    /// when adapter flags do not distinguish discrete from integrated.
    /// </summary>
    private const int DiscreteVramThresholdMb = 512;

    /// <summary>
    /// Default NPU memory estimate in MB when OpenVINO does not report constraints.
    /// </summary>
    private const int DefaultNpuMemoryMb = 50;

    public WindowsDeviceEnumerator(
        IOpenVinoAvailabilityProvider openVinoAvailability,
        ILogger<WindowsDeviceEnumerator> logger,
        Func<bool>? hasIntelNpuPciDevice = null)
    {
        _openVinoAvailability = openVinoAvailability ?? throw new ArgumentNullException(nameof(openVinoAvailability));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _hasIntelNpuPciDevice = hasIntelNpuPciDevice ?? HasIntelNpuPciDevice;
    }

    public async Task<IReadOnlyList<DeviceEntry>> GetDevicesAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var cached = _cachedDevices;
        if (cached is not null)
            return cached;

        return await EnumerateAndCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<DeviceEntry>> ReEnumerateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return await EnumerateAndCacheAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<DeviceEntry>> EnumerateAndCacheAsync(CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var devices = EnumerateDevices();
            _cachedDevices = devices;
            return devices;
        }
        finally
        {
            _semaphore.Release();
        }
    }

    private IReadOnlyList<DeviceEntry> EnumerateDevices()
    {
        var devices = new List<DeviceEntry>();
        int gpuDeviceIndex = 0;

        // NPU probes are called early to populate device entries.
        // Each probe is individually guarded so that a failure on one
        // does not prevent the others (or the CPU fallback) from running.
        bool hasQualcommNpu = false;
        try
        {
            hasQualcommNpu = HasQualcommNpu();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Qualcomm NPU detection failed; Qnn provider will not be advertised.");
        }

        bool hasAmdRyzenAiNpu = false;
        try
        {
            hasAmdRyzenAiNpu = HasAmdRyzenAiNpu();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AMD Ryzen AI NPU detection failed; VitisAi provider will not be advertised.");
        }

        try
        {
            gpuDeviceIndex = EnumerateGpuDevices(devices);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "DXGI adapter enumeration failed. Returning CPU-only device list.");
        }

        // Probe for NPUs with the same defensive posture as DXGI enumeration:
        // if any probe throws, log it and degrade gracefully rather than aborting
        // the entire device list (which would leave the user with zero devices).
        try
        {
            hasQualcommNpu = HasQualcommNpu();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Qualcomm NPU detection failed; skipping Qnn provider.");
        }

        try
        {
            hasAmdRyzenAiNpu = HasAmdRyzenAiNpu();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "AMD Ryzen AI NPU detection failed; skipping VitisAi provider.");
        }

        int nextDeviceIndex = gpuDeviceIndex;

        // NPU entry: only if OpenVINO NPU mode is active and an Intel NPU device is present.
        if (ShouldIncludeNpuDevice())
        {
            devices.Add(new DeviceEntry(
                Kind: DeviceKind.Npu,
                DeviceIndex: nextDeviceIndex++,
                AdapterDescription: "Intel NPU",
                VendorName: "Intel",
                DedicatedVramMb: DefaultNpuMemoryMb,
                SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.OpenVino]));
        }

        if (hasQualcommNpu)
        {
            devices.Add(new DeviceEntry(
                Kind: DeviceKind.Npu,
                DeviceIndex: nextDeviceIndex++,
                AdapterDescription: "Qualcomm Snapdragon NPU",
                VendorName: "Qualcomm",
                DedicatedVramMb: DefaultNpuMemoryMb,
                SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.Qnn]));
        }

        if (hasAmdRyzenAiNpu)
        {
            devices.Add(new DeviceEntry(
                Kind: DeviceKind.Npu,
                DeviceIndex: nextDeviceIndex++,
                AdapterDescription: "AMD Ryzen AI NPU",
                VendorName: "AMD",
                DedicatedVramMb: DefaultNpuMemoryMb,
                SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.VitisAi]));
        }

        int cpuDeviceIndex = nextDeviceIndex;

        // CPU entry: always present and assigned a unique index.
        // When UseOpenVinoCpuProxy is active, also advertise OpenVino support
        // so downstream provider routing can reach OpenVINO via CPU execution.
        var cpuSupportedProviders = new List<ExecutionProviderKind> { ExecutionProviderKind.Cpu };
        if (_openVinoAvailability.UseOpenVinoCpuProxy)
        {
            cpuSupportedProviders.Add(ExecutionProviderKind.OpenVino);
        }
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64 && Dnnl.DnnlOrtProbe.IsProviderListed())
        {
            cpuSupportedProviders.Add(ExecutionProviderKind.Dnnl);
        }

        devices.Add(new DeviceEntry(
            Kind: DeviceKind.Cpu,
            DeviceIndex: cpuDeviceIndex,
            AdapterDescription: "CPU",
            VendorName: "System",
            DedicatedVramMb: 0,
            SharedMemoryMb: 0,
            SupportedProviders: cpuSupportedProviders.AsReadOnly()));

        // Sort by kind priority (lower enum value = higher priority), then by device index ascending
        devices.Sort((a, b) =>
        {
            int kindCompare = a.Kind.CompareTo(b.Kind);
            return kindCompare != 0 ? kindCompare : a.DeviceIndex.CompareTo(b.DeviceIndex);
        });

        return devices.AsReadOnly();
    }

    private int EnumerateGpuDevices(List<DeviceEntry> devices)
    {
        int deviceIndex = 0;

        int hr = NativeMethods.CreateDXGIFactory1(ref NativeMethods.IID_IDXGIFactory1, out nint factoryPtr);
        if (hr < 0 || factoryPtr == 0)
        {
            _logger.LogError("CreateDXGIFactory1 failed with HRESULT 0x{HResult:X8}.", hr);
            return deviceIndex;
        }

        try
        {
            uint adapterIndex = 0;
            while (true)
            {
                hr = NativeMethods.IDXGIFactory1_EnumAdapters1(factoryPtr, adapterIndex, out nint adapterPtr);
                if (hr < 0)
                    break; // DXGI_ERROR_NOT_FOUND or other error — no more adapters

                try
                {
                    var entry = CreateDeviceEntryFromAdapter(adapterPtr, deviceIndex);
                    if (entry is not null)
                    {
                        devices.Add(entry);
                        deviceIndex++;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "Failed to query adapter at index {AdapterIndex}. Skipping.", adapterIndex);
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }

                adapterIndex++;
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }

        return deviceIndex;
    }

    private DeviceEntry? CreateDeviceEntryFromAdapter(nint adapterPtr, int deviceIndex)
    {
        var desc = new DxgiAdapterDesc1();
        int hr = NativeMethods.IDXGIAdapter1_GetDesc1(adapterPtr, ref desc);
        if (hr < 0)
        {
            _logger.LogWarning("IDXGIAdapter1_GetDesc1 failed with HRESULT 0x{HResult:X8}.", hr);
            return null;
        }

        // Skip software adapters (e.g., Microsoft Basic Render Driver)
        if ((desc.Flags & NativeMethods.DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
            return null;

        string adapterDescription = desc.GetDescription();
        string vendorName = ResolveVendorName(desc.VendorId);

        int dedicatedVramMb;
        int sharedMemoryMb;
        try
        {
            dedicatedVramMb = (int)(desc.DedicatedVideoMemory / (1024 * 1024));
            sharedMemoryMb = (int)(desc.SharedSystemMemory / (1024 * 1024));
        }
        catch
        {
            _logger.LogWarning("VRAM query failed for adapter '{Adapter}'. Setting VRAM to 0.", adapterDescription);
            dedicatedVramMb = 0;
            sharedMemoryMb = 0;
        }

        DeviceKind kind = ClassifyDeviceKind(desc.Flags, dedicatedVramMb);

        var supportedProviders = new List<ExecutionProviderKind> { ExecutionProviderKind.DirectMl };
        if (desc.VendorId == 0x1002)
        {
            supportedProviders.Insert(0, ExecutionProviderKind.Migraphx);
        }

        if (_openVinoAvailability.IsAvailable)
        {
            supportedProviders.Add(ExecutionProviderKind.OpenVino);
        }

        return new DeviceEntry(
            Kind: kind,
            DeviceIndex: deviceIndex,
            AdapterDescription: adapterDescription,
            VendorName: vendorName,
            DedicatedVramMb: dedicatedVramMb,
            SharedMemoryMb: sharedMemoryMb,
            SupportedProviders: supportedProviders.AsReadOnly(),
            AdapterLuid: desc.AdapterLuid);
    }

    /// <summary>
    /// Queries live available VRAM (Budget - CurrentUsage) for the adapter at <paramref name="adapterLuid"/>.
    /// Returns null if the adapter cannot be found or IDXGIAdapter3 is unavailable.
    /// </summary>
    public static long? QueryAvailableVramMb(long adapterLuid)
    {
        int hr = NativeMethods.CreateDXGIFactory1(ref NativeMethods.IID_IDXGIFactory1, out nint factoryPtr);
        if (hr < 0 || factoryPtr == 0)
            return null;

        try
        {
            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                hr = NativeMethods.IDXGIFactory1_EnumAdapters1(factoryPtr, adapterIndex, out nint adapterPtr);
                if (hr < 0)
                    break;

                try
                {
                    var desc = new DxgiAdapterDesc1();
                    hr = NativeMethods.IDXGIAdapter1_GetDesc1(adapterPtr, ref desc);
                    if (hr < 0)
                        continue;

                    if (desc.AdapterLuid != adapterLuid)
                        continue;

                    // QI for IDXGIAdapter3
                    var iid = NativeMethods.IID_IDXGIAdapter3;
                    hr = NativeMethods.IUnknown_QueryInterface(adapterPtr, ref iid, out nint adapter3Ptr);
                    if (hr < 0 || adapter3Ptr == 0)
                        return null;

                    try
                    {
                        hr = NativeMethods.IDXGIAdapter3_QueryVideoMemoryInfo(
                            adapter3Ptr, 0, 0 /* DXGI_MEMORY_SEGMENT_GROUP_LOCAL */, out var info);
                        if (hr < 0)
                            return null;

                        ulong available = info.Budget > info.CurrentUsage
                            ? info.Budget - info.CurrentUsage
                            : 0;
                        return (long)(available / (1024 * 1024));
                    }
                    finally
                    {
                        Marshal.Release(adapter3Ptr);
                    }
                }
                finally
                {
                    Marshal.Release(adapterPtr);
                }
            }
        }
        finally
        {
            Marshal.Release(factoryPtr);
        }

        return null;
    }

    internal static DeviceKind ClassifyDeviceKind(uint adapterFlags, int dedicatedVramMb)
    {
        // If the adapter is flagged as non-detachable (integrated), classify as IntegratedGpu
        // DXGI doesn't have a direct "discrete" flag, but DXGI_ADAPTER_FLAG_NONE (0) typically
        // indicates a discrete adapter. We use the VRAM heuristic as fallback.
        // Note: DXGI_ADAPTER_FLAG_SOFTWARE is already filtered out before this method is called.

        // If flags don't clearly indicate discrete vs integrated, use VRAM heuristic
        if (dedicatedVramMb > DiscreteVramThresholdMb)
            return DeviceKind.DiscreteGpu;

        return DeviceKind.IntegratedGpu;
    }

    private bool ShouldIncludeNpuDevice()
    {
        if (!_openVinoAvailability.IsAvailable)
        {
            return false;
        }

        if (_openVinoAvailability.UseOpenVinoCpuProxy)
        {
            return false;
        }

        return _hasIntelNpuPciDevice();
    }

    private bool HasIntelNpuPciDevice()
    {
        try
        {
            using var pciRoot = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciRoot is null)
            {
                return false;
            }

            foreach (string deviceId in pciRoot.GetSubKeyNames())
            {
                using var deviceKey = pciRoot.OpenSubKey(deviceId);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (string instanceId in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceId);
                    if (instanceKey is null)
                    {
                        continue;
                    }

                    string details = string.Join(' ',
                        GetRegistryString(instanceKey, "FriendlyName"),
                        GetRegistryString(instanceKey, "DeviceDesc"),
                        GetRegistryString(instanceKey, "Mfg"),
                        GetRegistryString(instanceKey, "Service"),
                        GetRegistryString(instanceKey, "Class"));

                    bool looksLikeNpu = ContainsAny(details,
                        "npu",
                        "neural",
                        "ai boost",
                        "intelnpu");

                    bool isIntelDevice = details.Contains("intel", StringComparison.OrdinalIgnoreCase)
                        || deviceId.Contains("VEN_8086", StringComparison.OrdinalIgnoreCase);

                    if (looksLikeNpu && isIntelDevice)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Intel NPU detection via PCI registry failed.");
            return false;
        }
    }

    private static bool HasQualcommNpu()
    {
        try
        {
            using var pciRoot = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciRoot is null)
            {
                return false;
            }

            foreach (string deviceId in pciRoot.GetSubKeyNames())
            {
                using var deviceKey = pciRoot.OpenSubKey(deviceId);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (string instanceId in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceId);
                    if (instanceKey is null)
                    {
                        continue;
                    }

                    string details = string.Join(' ',
                        GetRegistryString(instanceKey, "FriendlyName"),
                        GetRegistryString(instanceKey, "DeviceDesc"),
                        GetRegistryString(instanceKey, "Mfg"));

                    bool isQualcommDevice = details.Contains("qualcomm", StringComparison.OrdinalIgnoreCase)
                        || details.Contains("snapdragon", StringComparison.OrdinalIgnoreCase)
                        || deviceId.Contains("VEN_17CB", StringComparison.OrdinalIgnoreCase);

                    bool looksLikeNpu = ContainsAny(details,
                        "npu",
                        "neural",
                        "ai engine",
                        "hexagon");

                    if (isQualcommDevice && looksLikeNpu)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool HasAmdRyzenAiNpu()
    {
        try
        {
            using var pciRoot = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SYSTEM\CurrentControlSet\Enum\PCI");
            if (pciRoot is null)
            {
                return false;
            }

            foreach (string deviceId in pciRoot.GetSubKeyNames())
            {
                using var deviceKey = pciRoot.OpenSubKey(deviceId);
                if (deviceKey is null)
                {
                    continue;
                }

                foreach (string instanceId in deviceKey.GetSubKeyNames())
                {
                    using var instanceKey = deviceKey.OpenSubKey(instanceId);
                    if (instanceKey is null)
                    {
                        continue;
                    }

                    string details = string.Join(' ',
                        GetRegistryString(instanceKey, "FriendlyName"),
                        GetRegistryString(instanceKey, "DeviceDesc"),
                        GetRegistryString(instanceKey, "Mfg"));

                    bool isAmdDevice = details.Contains("amd", StringComparison.OrdinalIgnoreCase)
                        || details.Contains("ryzen ai", StringComparison.OrdinalIgnoreCase)
                        || deviceId.Contains("VEN_1022", StringComparison.OrdinalIgnoreCase);

                    bool looksLikeNpu = ContainsAny(details,
                        "npu",
                        "ryzen ai",
                        "xilinx",
                        "vitis");

                    if (isAmdDevice && looksLikeNpu)
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string GetRegistryString(Microsoft.Win32.RegistryKey key, string valueName)
    {
        return key.GetValue(valueName) as string ?? string.Empty;
    }

    private static bool ContainsAny(string value, params string[] terms)
    {
        foreach (string term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsIntelCpu()
    {
        try
        {
            string? processorId = Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");
            if (processorId is not null &&
                processorId.Contains("Intel", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            // Fallback: check processor brand via registry
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            var brand = key?.GetValue("ProcessorNameString") as string;
            return brand?.Contains("Intel", StringComparison.OrdinalIgnoreCase) ?? false;
        }
        catch
        {
            return false;
        }
    }

    private static string ResolveVendorName(uint vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft",
        _ => $"Unknown (0x{vendorId:X4})"
    };

    /// <summary>
    /// Native DXGI interop methods and structures for adapter enumeration.
    /// </summary>
    private static class NativeMethods
    {
        public static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll", ExactSpelling = true, PreserveSig = true)]
        public static extern int CreateDXGIFactory1(ref Guid riid, out nint ppFactory);

        /// <summary>
        /// Calls IDXGIFactory1::EnumAdapters1 via vtable slot 12.
        /// </summary>
        public static int IDXGIFactory1_EnumAdapters1(nint factory, uint adapterIndex, out nint adapter)
        {
            // IDXGIFactory1 vtable layout:
            // IUnknown: 0=QueryInterface, 1=AddRef, 2=Release
            // IDXGIObject: 3=SetPrivateData, 4=SetPrivateDataInterface, 5=GetPrivateData, 6=GetParent
            // IDXGIFactory: 7=EnumAdapters, 8=MakeWindowAssociation, 9=GetWindowAssociation, 10=CreateSwapChain, 11=CreateSoftwareAdapter
            // IDXGIFactory1: 12=EnumAdapters1, 13=IsCurrent
            nint vtable = Marshal.ReadIntPtr(factory);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 12 * nint.Size);

            var fn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(fnPtr);
            return fn(factory, adapterIndex, out adapter);
        }

        /// <summary>
        /// Calls IDXGIAdapter1::GetDesc1 via vtable slot 10.
        /// </summary>
        public static int IDXGIAdapter1_GetDesc1(nint adapter, ref DxgiAdapterDesc1 desc)
        {
            // IDXGIAdapter1 vtable layout:
            // IUnknown: 0=QueryInterface, 1=AddRef, 2=Release
            // IDXGIObject: 3=SetPrivateData, 4=SetPrivateDataInterface, 5=GetPrivateData, 6=GetParent
            // IDXGIAdapter: 7=EnumOutputs, 8=GetDesc, 9=CheckInterfaceSupport
            // IDXGIAdapter1: 10=GetDesc1
            nint vtable = Marshal.ReadIntPtr(adapter);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 10 * nint.Size);

            var fn = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(fnPtr);
            return fn(adapter, ref desc);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EnumAdapters1Delegate(nint thisPtr, uint adapterIndex, out nint adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetDesc1Delegate(nint thisPtr, ref DxgiAdapterDesc1 desc);

        // ── IDXGIAdapter3 support (VRAM budget queries) ──────────────────────────

        // IID_IDXGIAdapter3 = {645967A4-1392-4310-A798-8053CE3E93FD}
        public static Guid IID_IDXGIAdapter3 = new("645967A4-1392-4310-A798-8053CE3E93FD");

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int QueryInterfaceDelegate(nint thisPtr, ref Guid riid, out nint ppvObject);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int QueryVideoMemoryInfoDelegate(nint thisPtr, uint nodeIndex, uint memorySegmentGroup, out DxgiQueryVideoMemoryInfo pVideoMemoryInfo);

        [StructLayout(LayoutKind.Sequential)]
        public struct DxgiQueryVideoMemoryInfo
        {
            public ulong Budget;
            public ulong CurrentUsage;
            public ulong AvailableForReservation;
            public ulong CurrentReservation;
        }

        public static int IUnknown_QueryInterface(nint obj, ref Guid riid, out nint ppvObject)
        {
            nint vtable = Marshal.ReadIntPtr(obj);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 0 * nint.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(fnPtr);
            return fn(obj, ref riid, out ppvObject);
        }

        public static int IDXGIAdapter3_QueryVideoMemoryInfo(nint adapter3, uint nodeIndex, uint memorySegmentGroup, out DxgiQueryVideoMemoryInfo info)
        {
            nint vtable = Marshal.ReadIntPtr(adapter3);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 14 * nint.Size);
            var fn = Marshal.GetDelegateForFunctionPointer<QueryVideoMemoryInfoDelegate>(fnPtr);
            return fn(adapter3, nodeIndex, memorySegmentGroup, out info);
        }
    }

    /// <summary>
    /// Mirrors the native DXGI_ADAPTER_DESC1 structure.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DxgiAdapterDesc1
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string Description;

        public uint VendorId;
        public uint DeviceId;
        public uint SubSysId;
        public uint Revision;
        public nuint DedicatedVideoMemory;
        public nuint DedicatedSystemMemory;
        public nuint SharedSystemMemory;
        public long AdapterLuid;
        public uint Flags;

        public readonly string GetDescription() =>
            Description?.TrimEnd('\0') ?? string.Empty;
    }
}
