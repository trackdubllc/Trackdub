using System.Runtime.Versioning;

namespace Trackdub.Inference.Onnx.Runtime;

/// <summary>
/// Parses /sys/bus/pci/devices to enumerate GPU-class PCI devices on Linux.
/// Shared between LinuxDeviceEnumerator and MachineHardwareProfileProvider.
/// </summary>
[SupportedOSPlatform("linux")]
internal static class LinuxPciDeviceScanner
{
    // PCI class codes for display/GPU devices (upper 3 nibbles of the 6-hex-digit class field)
    private const uint PciClassVga = 0x030000;
    private const uint PciClass3DCtrl = 0x030200;
    private const uint PciClassDisplay = 0x038000;

    private const uint VendorNvidia = 0x10de;
    private const uint VendorAmd = 0x1002;
    private const uint VendorIntel = 0x8086;

    internal sealed record PciGpuDevice(string Address, GpuVendor Vendor, long VramMb);

    internal enum GpuVendor { Unknown, Nvidia, Amd, Intel }

    internal static IReadOnlyList<PciGpuDevice> EnumerateGpus(ISysfsReader sysfs)
    {
        const string pciBase = "/sys/bus/pci/devices";
        var result = new List<PciGpuDevice>();

        foreach (string deviceDir in sysfs.EnumerateDirectories(pciBase))
        {
            string classPath = Path.Combine(deviceDir, "class");
            string? classText = sysfs.ReadAllText(classPath)?.Trim();
            if (classText is null || !TryParseHex(classText, out uint classCode))
                continue;

            uint classGroup = classCode & 0xFFFF00;
            if (classGroup != PciClassVga && classGroup != PciClass3DCtrl && classGroup != PciClassDisplay)
                continue;

            string address = Path.GetFileName(deviceDir);
            GpuVendor vendor = ReadVendor(sysfs, deviceDir);
            long vramMb = ReadVramMb(sysfs, deviceDir, address, vendor);
            result.Add(new PciGpuDevice(address, vendor, vramMb));
        }

        return result;
    }

    internal static bool HasIntelNpu(ISysfsReader sysfs) =>
        sysfs.DirectoryExists("/sys/bus/pci/drivers/intel_vpu") &&
        sysfs.EnumerateDirectories("/sys/bus/pci/drivers/intel_vpu").Any();

    internal static bool HasAnyGpu(ISysfsReader sysfs) => EnumerateGpus(sysfs).Count > 0;

    internal static string? DetectPrimaryVendorName(ISysfsReader sysfs)
    {
        IReadOnlyList<PciGpuDevice> gpus = EnumerateGpus(sysfs);
        if (gpus.Count == 0) return null;
        return gpus[0].Vendor switch
        {
            GpuVendor.Nvidia => "NVIDIA",
            GpuVendor.Amd => "AMD",
            GpuVendor.Intel => "Intel",
            _ => "Unknown",
        };
    }

    private static GpuVendor ReadVendor(ISysfsReader sysfs, string deviceDir)
    {
        string? vendorText = sysfs.ReadAllText(Path.Combine(deviceDir, "vendor"))?.Trim();
        if (vendorText is null || !TryParseHex(vendorText, out uint vendorId))
            return GpuVendor.Unknown;
        return vendorId switch
        {
            VendorNvidia => GpuVendor.Nvidia,
            VendorAmd => GpuVendor.Amd,
            VendorIntel => GpuVendor.Intel,
            _ => GpuVendor.Unknown,
        };
    }

    private static long ReadVramMb(ISysfsReader sysfs, string deviceDir, string address, GpuVendor vendor)
    {
        return vendor switch
        {
            GpuVendor.Nvidia => ReadNvidiaVramMb(sysfs, address),
            GpuVendor.Amd => ReadAmdVramMb(sysfs, deviceDir),
            _ => 0,
        };
    }

    private static long ReadNvidiaVramMb(ISysfsReader sysfs, string address)
    {
        string infoPath = $"/proc/driver/nvidia/gpus/{address}/information";
        string? text = sysfs.ReadAllText(infoPath);
        if (text is null) return 0;

        foreach (string line in text.Split('\n'))
        {
            if (!line.StartsWith("Video Memory:", StringComparison.OrdinalIgnoreCase))
                continue;

            string valuePart = line["Video Memory:".Length..].Trim();
            // Format: "8192 MB" or "8192MB"
            string[] parts = valuePart.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length >= 1 && long.TryParse(parts[0], out long mb))
                return mb;
        }
        return 0;
    }

    private static long ReadAmdVramMb(ISysfsReader sysfs, string deviceDir)
    {
        // Find the DRM card link for this PCI device
        string? symlink = sysfs.EnumerateDirectories(deviceDir)
            .FirstOrDefault(d => Path.GetFileName(d).StartsWith("drm", StringComparison.Ordinal));
        if (symlink is null) return 0;

        string? bytesText = sysfs.ReadAllText(
            Path.Combine(deviceDir, "mem_info_vram_total"));
        if (bytesText is null) return 0;

        return long.TryParse(bytesText.Trim(), out long bytes) ? bytes / 1024 / 1024 : 0;
    }

    private static bool TryParseHex(string text, out uint value)
    {
        ReadOnlySpan<char> span = text.AsSpan().TrimStart("0x").TrimStart("0X");
        return uint.TryParse(span, System.Globalization.NumberStyles.HexNumber, null, out value);
    }
}
