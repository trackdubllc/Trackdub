using System.Runtime.Versioning;
using System.Text;
using Microsoft.Win32;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Components.NvidiaAfx;

public interface INvidiaAfxArchitectureDetector
{
    string DetectArchitectureBucket();

    /// <summary>
    /// Architecture buckets to try, best first and distinct. A machine can list more than one
    /// NVIDIA adapter (a stale entry left by a GPU swap, or two generations installed) and the
    /// registry does not say which one CUDA device 0 is, so callers try each candidate and let
    /// the native probe decide. Empty when there is no supported NVIDIA adapter.
    /// </summary>
    IReadOnlyList<string> DetectArchitectureBuckets()
    {
        string bucket = DetectArchitectureBucket();
        return string.Equals(bucket, "unsupported", StringComparison.Ordinal) ? [] : [bucket];
    }
}

public sealed class NvidiaAfxArchitectureDetector : INvidiaAfxArchitectureDetector
{
    private const string DisplayAdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public string DetectArchitectureBucket()
    {
        IReadOnlyList<string> buckets = DetectArchitectureBuckets();
        return buckets.Count > 0 ? buckets[0] : "unsupported";
    }

    public IReadOnlyList<string> DetectArchitectureBuckets()
    {
        if (!OperatingSystem.IsWindows())
        {
            return [];
        }

        string gpuName = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            return [DetectOverrideArchitectureBucket(gpuName)];
        }

        return BucketsFromAdapterNames(ReadDisplayAdapterNames());
    }

    internal static string DetectOverrideArchitectureBucket(string gpuName)
    {
        NvidiaGpuArchitectureBucket architecture = NvidiaGpuArchitectureClassifier.ClassifyFromName(gpuName);
        return architecture is NvidiaGpuArchitectureBucket.Unknown
            ? "turing"
            : NvidiaGpuArchitectureClassifier.ToAfxArchitectureBucket(architecture);
    }

    internal static string DetectFromAdapterNames(IEnumerable<string> adapterNames)
    {
        IReadOnlyList<string> buckets = BucketsFromAdapterNames(adapterNames);
        return buckets.Count > 0 ? buckets[0] : "unsupported";
    }

    /// <summary>
    /// Every NVIDIA adapter with Tensor Cores, newest architecture first. GTX cards have none, so
    /// they are not candidates even though the shared classifier files unknown NVIDIA names under Turing.
    /// </summary>
    internal static IReadOnlyList<string> BucketsFromAdapterNames(IEnumerable<string> adapterNames) =>
        [.. adapterNames
            .Where(name => name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase)
                           && !name.Contains("GTX", StringComparison.OrdinalIgnoreCase))
            .Select(name => NvidiaGpuArchitectureClassifier.ClassifyFromName(name))
            .Where(architecture => architecture is not NvidiaGpuArchitectureBucket.Unknown)
            .Distinct()
            .OrderByDescending(architecture => architecture)
            .Select(architecture => NvidiaGpuArchitectureClassifier.ToAfxArchitectureBucket(architecture))];

    internal static string DecodeAdapterString(byte[] bytes) =>
        Encoding.Unicode.GetString(bytes).TrimEnd('\0');

    [SupportedOSPlatform("windows")]
    private static List<string> ReadDisplayAdapterNames()
    {
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey);
            return classKey is null
                ? []
                : classKey.GetSubKeyNames()
                    .Select(subkeyName => ReadAdapterName(classKey, subkeyName))
                    .OfType<string>()
                    .ToList();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable registry: treat as no detectable NVIDIA adapter rather than guessing.
            return [];
        }
    }

    // The class key also holds subkeys a standard user cannot open (Properties and similar), so a
    // failure on one subkey must skip that subkey, never discard the adapters already read.
    [SupportedOSPlatform("windows")]
    private static string? ReadAdapterName(RegistryKey classKey, string subkeyName)
    {
        try
        {
            using RegistryKey? adapterKey = classKey.OpenSubKey(subkeyName);
            if (adapterKey is null)
            {
                return null;
            }

            // Some drivers leave DriverDesc empty and only store the name in HardwareInformation.
            string? name = adapterKey.GetValue("DriverDesc") as string
                ?? adapterKey.GetValue("HardwareInformation.AdapterString") switch
                {
                    string text => text,
                    byte[] bytes => DecodeAdapterString(bytes),
                    _ => null,
                };
            return string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            return null;
        }
    }
}
