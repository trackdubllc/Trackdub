using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Runtime;

[SupportedOSPlatform("macos10.15")]
public sealed class MacDeviceEnumerator : IDeviceEnumerator
{
    private readonly ILogger<MacDeviceEnumerator> _logger;
    private readonly SemaphoreSlim _semaphore = new(1, 1);
    private IReadOnlyList<DeviceEntry>? _cachedDevices;

    // Metal framework
    [DllImport("/System/Library/Frameworks/Metal.framework/Metal")]
    private static extern IntPtr MTLCopyAllDevices();

    // Objective-C runtime messaging
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint CallGetCount(IntPtr self, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr CallObjectAtIndex(IntPtr self, IntPtr sel, nuint index);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr CallGetName(IntPtr self, IntPtr sel);

    // recommendedMaxWorkingSetSize returns NSUInteger — pointer-width, use nuint
    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern nuint CallGetMaxWorkingSetSize(IntPtr self, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern bool CallGetHasUnifiedMemory(IntPtr self, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static extern IntPtr CallGetUtf8String(IntPtr self, IntPtr sel);

    [DllImport("/usr/lib/libobjc.A.dylib")]
    private static extern IntPtr sel_registerName(string name);

    public MacDeviceEnumerator(ILogger<MacDeviceEnumerator> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
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
        try
        {
            var profilerEntries = EnumerateViaSystemProfiler();
            if (profilerEntries.Any(e => e.Kind != DeviceKind.Cpu))
                return profilerEntries;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "system_profiler enumeration failed; trying Metal P/Invoke.");
        }

        try
        {
            return EnumerateViaMetalPInvoke();
        }
        catch (DllNotFoundException ex)
        {
            _logger.LogWarning(ex, "Metal framework not found; falling back to CPU-only entry.");
            return FallbackCpuEntry();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Metal P/Invoke failed; falling back to CPU-only entry.");
            return FallbackCpuEntry();
        }
    }

    private IReadOnlyList<DeviceEntry> EnumerateViaMetalPInvoke()
    {
        IntPtr deviceArray = MTLCopyAllDevices();
        if (deviceArray == IntPtr.Zero)
            return FallbackCpuEntry();

        IntPtr selCount = sel_registerName("count");
        IntPtr selObjectAtIndex = sel_registerName("objectAtIndex:");
        IntPtr selName = sel_registerName("name");
        IntPtr selMaxWorkingSet = sel_registerName("recommendedMaxWorkingSetSize");
        IntPtr selHasUnified = sel_registerName("hasUnifiedMemory");
        IntPtr selUtf8String = sel_registerName("UTF8String");

        nuint count = CallGetCount(deviceArray, selCount);
        var entries = new List<DeviceEntry>((int)count + 1);

        for (nuint i = 0; i < count; i++)
        {
            IntPtr device = GetDeviceAtIndexSafe(deviceArray, selObjectAtIndex, i, count);
            if (device == IntPtr.Zero) continue;

            IntPtr nameNs = CallGetName(device, selName);
            string name = TryGetNSStringUtf8(nameNs, selUtf8String);

            nuint workingSetBytes = TryGetMaxWorkingSetSize(device, selMaxWorkingSet);
            long vramMb = (long)(workingSetBytes / 1024 / 1024);
            bool hasUnified = TryGetHasUnifiedMemory(device, selHasUnified);

            string vendor = InferVendorFromName(name);
            DeviceKind kind = hasUnified ? DeviceKind.IntegratedGpu : DeviceKind.DiscreteGpu;

            entries.Add(new DeviceEntry(
                Kind: kind,
                DeviceIndex: (int)i,
                AdapterDescription: name,
                VendorName: vendor,
                DedicatedVramMb: (int)Math.Min(vramMb, int.MaxValue),
                SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.CoreMl]));

            _logger.LogDebug(
                "Metal GPU [{Index}]: {Name}, unified={Unified}, workingSet={VramMb}MB",
                i, name, hasUnified, vramMb);
        }

        entries.Add(CpuEntry(entries.Count));
        return entries;
    }

    private static IntPtr GetDeviceAtIndexSafe(IntPtr deviceArray, IntPtr selObjectAtIndex, nuint index, nuint count)
    {
        if (deviceArray == IntPtr.Zero || selObjectAtIndex == IntPtr.Zero)
            return IntPtr.Zero;

        if (index >= count)
            return IntPtr.Zero;

        return CallObjectAtIndex(deviceArray, selObjectAtIndex, index);
    }

    private IReadOnlyList<DeviceEntry> EnumerateViaSystemProfiler()
    {
        try
        {
            using var process = new System.Diagnostics.Process
            {
                StartInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "system_profiler",
                    Arguments = "SPDisplaysDataType -json",
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                },
            };

            process.Start();
            string json = process.StandardOutput.ReadToEnd();
            process.WaitForExit();

            return ParseSystemProfilerJson(json);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "system_profiler fallback failed; returning CPU-only device list.");
            return FallbackCpuEntry();
        }
    }

    private List<DeviceEntry> ParseSystemProfilerJson(string json)
    {
        var entries = new List<DeviceEntry>();

        using JsonDocument doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("SPDisplaysDataType", out JsonElement displays))
            return [CpuEntry(0)];

        int index = 0;
        foreach (JsonElement display in displays.EnumerateArray())
        {
            string name = display.TryGetProperty("sppci_model", out JsonElement modelEl)
                ? modelEl.GetString() ?? "Mac GPU"
                : "Mac GPU";

            // spdisplays_vram is localized and absent on Apple Silicon — treat as advisory only
            long vramMb = 0;
            if (display.TryGetProperty("spdisplays_vram", out JsonElement vramEl))
            {
                string? vramStr = vramEl.GetString();
                if (vramStr is not null)
                {
                    // Format: "8192 MB" or "8 GB" etc.
                    string[] parts = vramStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                    if (parts.Length >= 2 && long.TryParse(parts[0], out long val))
                    {
                        vramMb = parts[1].StartsWith("GB", StringComparison.OrdinalIgnoreCase)
                            ? val * 1024
                            : val;
                    }
                }
            }

            string vendor = InferVendorFromName(name);
            entries.Add(new DeviceEntry(
                Kind: DeviceKind.IntegratedGpu,
                DeviceIndex: index++,
                AdapterDescription: name,
                VendorName: vendor,
                DedicatedVramMb: (int)Math.Min(vramMb, int.MaxValue),
                SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.CoreMl]));
        }

        if (entries.Count == 0)
            entries.Add(new DeviceEntry(
                Kind: DeviceKind.IntegratedGpu, DeviceIndex: 0,
                AdapterDescription: "Mac GPU (Metal)", VendorName: "Apple",
                DedicatedVramMb: 0, SharedMemoryMb: 0,
                SupportedProviders: [ExecutionProviderKind.CoreMl]));

        entries.Add(CpuEntry(entries.Count));
        return entries;
    }

    private static string InferVendorFromName(string name)
    {
        if (name.Contains("Apple", StringComparison.OrdinalIgnoreCase)) return "Apple";
        if (name.Contains("AMD", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("Radeon", StringComparison.OrdinalIgnoreCase)) return "AMD";
        if (name.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel";
        if (name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)) return "NVIDIA";
        return "Unknown";
    }

    private static DeviceEntry CpuEntry(int index)
    {
        var providers = new List<ExecutionProviderKind> { ExecutionProviderKind.Cpu };
        if (RuntimeInformation.ProcessArchitecture == Architecture.X64 && Dnnl.DnnlOrtProbe.IsProviderListed())
        {
            providers.Add(ExecutionProviderKind.Dnnl);
        }

        return new DeviceEntry(
            Kind: DeviceKind.Cpu,
            DeviceIndex: index,
            AdapterDescription: "CPU",
            VendorName: "Generic",
            DedicatedVramMb: 0,
            SharedMemoryMb: 0,
            SupportedProviders: providers);
    }

    private static IReadOnlyList<DeviceEntry> FallbackCpuEntry() => [CpuEntry(0)];

    private nuint TryGetMaxWorkingSetSize(IntPtr device, IntPtr selMaxWorkingSet)
    {
        if (device == IntPtr.Zero || selMaxWorkingSet == IntPtr.Zero)
            return 0;

        try
        {
            return CallGetMaxWorkingSetSize(device, selMaxWorkingSet);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to query recommendedMaxWorkingSetSize for Metal device.");
            return 0;
        }
    }

    private bool TryGetHasUnifiedMemory(IntPtr device, IntPtr selHasUnified)
    {
        if (device == IntPtr.Zero || selHasUnified == IntPtr.Zero)
            return false;

        try
        {
            return CallGetHasUnifiedMemory(device, selHasUnified);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to read Metal hasUnifiedMemory; defaulting to false.");
            return false;
        }
    }

    private string TryGetNSStringUtf8(IntPtr nsString, IntPtr selUtf8String)
    {
        if (nsString == IntPtr.Zero || selUtf8String == IntPtr.Zero)
            return "Unknown GPU";

        try
        {
            IntPtr utf8Ptr = CallGetUtf8String(nsString, selUtf8String);
            if (utf8Ptr == IntPtr.Zero)
                return "Unknown GPU";

            return Marshal.PtrToStringUTF8(utf8Ptr) ?? "Unknown GPU";
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to marshal Metal device name from NSString.");
            return "Unknown GPU";
        }
    }
}
