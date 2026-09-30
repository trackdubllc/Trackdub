#if WINDOWS
using System.Runtime.InteropServices;
#endif

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// DI-free GPU VRAM probes for static pool defaults. Self-contained (raw DXGI
/// P/Invoke on Windows, plain /sys + /proc file reads on Linux) so the portable
/// TFM, which excludes the full device enumerators, can still scale the
/// accelerator admission budget. Every probe returns 0 on unknown/failure and
/// the caller treats 0 as unknown (floor budget).
/// </summary>
internal static class AcceleratorVramProbe
{
#if WINDOWS
    /// <summary>
    /// Largest dedicated video memory (MB) across hardware DXGI adapters.
    /// Windows leg only.
    /// </summary>
    internal static long QueryMaxDedicatedVramMb()
    {
        try
        {
            int hr = NativeMethods.CreateDXGIFactory1(ref NativeMethods.IID_IDXGIFactory1, out nint factoryPtr);
            if (hr < 0 || factoryPtr == 0)
                return 0;

            try
            {
                ulong maxBytes = 0;
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

                        if ((desc.Flags & NativeMethods.DXGI_ADAPTER_FLAG_SOFTWARE) != 0)
                            continue;

                        if (desc.DedicatedVideoMemory > maxBytes)
                            maxBytes = desc.DedicatedVideoMemory;
                    }
                    catch
                    {
                        continue;
                    }
                    finally
                    {
                        Marshal.Release(adapterPtr);
                    }
                }

                return (long)(maxBytes / (1024 * 1024));
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }
        }
        catch
        {
            return 0;
        }
    }
#endif

    /// <summary>
    /// Largest dedicated video memory (MB) across PCI GPU devices on Linux:
    /// NVIDIA via /proc/driver/nvidia, AMD via amdgpu sysfs, Intel iGPU reports 0
    /// (shared memory — floor budget applies). Pure managed file IO so it compiles
    /// on every leg; returns 0 off-Linux or on any failure.
    /// </summary>
    internal static long QueryLinuxMaxDedicatedVramMb()
    {
        if (!OperatingSystem.IsLinux())
            return 0;

        try
        {
            const string pciBase = "/sys/bus/pci/devices";
            if (!Directory.Exists(pciBase))
                return 0;

            long maxMb = 0;
            foreach (string deviceDir in Directory.EnumerateDirectories(pciBase))
            {
                try
                {
                    string? classText = ReadTextFile(Path.Join(deviceDir, "class"))?.Trim();
                    if (classText is null || !TryParseHex(classText, out uint classCode))
                        continue;

                    uint classGroup = classCode & 0xFFFF00;
                    if (classGroup != PciClassVga && classGroup != PciClass3DCtrl && classGroup != PciClassDisplay)
                        continue;

                    string? vendorText = ReadTextFile(Path.Join(deviceDir, "vendor"))?.Trim();
                    if (vendorText is null || !TryParseHex(vendorText, out uint vendorId))
                        continue;

                    long vramMb = vendorId switch
                    {
                        VendorNvidia => ReadNvidiaVramMb(Path.GetFileName(deviceDir)),
                        VendorAmd => ReadAmdVramMb(deviceDir),
                        _ => 0,
                    };
                    if (vramMb > maxMb)
                        maxMb = vramMb;
                }
                catch
                {
                    continue;
                }
            }

            return maxMb;
        }
        catch
        {
            return 0;
        }
    }

    private const uint PciClassVga = 0x030000;
    private const uint PciClass3DCtrl = 0x030200;
    private const uint PciClassDisplay = 0x038000;

    private const uint VendorNvidia = 0x10de;
    private const uint VendorAmd = 0x1002;

    private static long ReadNvidiaVramMb(string address)
    {
        // /proc format: "Video Memory:      8192 MB"
        string? text = ReadTextFile($"/proc/driver/nvidia/gpus/{address}/information");
        return text is null ? 0 : ParseNvidiaInformationMb(text);
    }

    internal static long ParseNvidiaInformationMb(string text)
    {
        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("Video Memory:", StringComparison.OrdinalIgnoreCase))
                continue;

            // Formats observed: "8192 MB", "8192MB", "8192 MiB".
            string token = line["Video Memory:".Length..].Trim()
                .Split(' ', StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            token = token.TrimEnd("mMbBiI".ToCharArray());
            if (long.TryParse(token, out long mb) && mb > 0)
                return mb;
        }

        return 0;
    }

    private static long ReadAmdVramMb(string deviceDir)
    {
        string? bytesText = null;
        try
        {
            string? drmLink = Directory.EnumerateDirectories(deviceDir)
                .FirstOrDefault(d => Path.GetFileName(d).StartsWith("drm", StringComparison.Ordinal));
            if (drmLink is null)
                return 0;

            bytesText = ReadTextFile(Path.Join(deviceDir, "mem_info_vram_total"));
        }
        catch
        {
            return 0;
        }

        return ParseAmdVramBytes(bytesText);
    }

    internal static long ParseAmdVramBytes(string? bytesText) =>
        bytesText is not null && long.TryParse(bytesText.Trim(), out long bytes) && bytes > 0
            ? bytes / 1024 / 1024
            : 0;

    internal static bool TryParseHex(string text, out uint value)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart("0x").TrimStart("0X");
        return uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);
    }

    private static string? ReadTextFile(string path)
    {
        try
        {
            return File.Exists(path) ? File.ReadAllText(path) : null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

#if WINDOWS
    private static class NativeMethods
    {
        public static Guid IID_IDXGIFactory1 = new("770aae78-f26f-4dba-a829-253c83d1b387");

        public const uint DXGI_ADAPTER_FLAG_SOFTWARE = 2;

        [DllImport("dxgi.dll", ExactSpelling = true, PreserveSig = true)]
        public static extern int CreateDXGIFactory1(ref Guid riid, out nint ppFactory);

        public static int IDXGIFactory1_EnumAdapters1(nint factory, uint adapterIndex, out nint adapter)
        {
            // IDXGIFactory1::EnumAdapters1 is vtable slot 12.
            nint vtable = Marshal.ReadIntPtr(factory);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 12 * nint.Size);

            var fn = Marshal.GetDelegateForFunctionPointer<EnumAdapters1Delegate>(fnPtr);
            return fn(factory, adapterIndex, out adapter);
        }

        public static int IDXGIAdapter1_GetDesc1(nint adapter, ref DxgiAdapterDesc1 desc)
        {
            // IDXGIAdapter1::GetDesc1 is vtable slot 10.
            nint vtable = Marshal.ReadIntPtr(adapter);
            nint fnPtr = Marshal.ReadIntPtr(vtable, 10 * nint.Size);

            var fn = Marshal.GetDelegateForFunctionPointer<GetDesc1Delegate>(fnPtr);
            return fn(adapter, ref desc);
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int EnumAdapters1Delegate(nint thisPtr, uint adapterIndex, out nint adapter);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        public delegate int GetDesc1Delegate(nint thisPtr, ref DxgiAdapterDesc1 desc);
    }

    /// <summary>
    /// Mirrors the native DXGI_ADAPTER_DESC1 prefix through DedicatedVideoMemory;
    /// field order must match the native layout.
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
    }
#endif
}
