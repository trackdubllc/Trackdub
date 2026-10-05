using System.Runtime.Versioning;
using Microsoft.Win32;
using Trackdub.Domain;

namespace Trackdub.Infrastructure.Components.NvidiaAfx;

public interface INvidiaAfxArchitectureDetector
{
    string DetectArchitectureBucket();
}

public sealed class NvidiaAfxArchitectureDetector : INvidiaAfxArchitectureDetector
{
    private const string DisplayAdapterClassKey =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";

    public string DetectArchitectureBucket()
    {
        if (!OperatingSystem.IsWindows())
        {
            return "unsupported";
        }

        string gpuName = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME") ?? string.Empty;
        if (!string.IsNullOrWhiteSpace(gpuName))
        {
            return DetectOverrideArchitectureBucket(gpuName);
        }

        return DetectFromAdapterNames(ReadDisplayAdapterNames());
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
        string? nvidiaAdapter = adapterNames.FirstOrDefault(name =>
            name.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase));
        return nvidiaAdapter is null
            ? "unsupported"
            : DetectOverrideArchitectureBucket(nvidiaAdapter);
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ReadDisplayAdapterNames()
    {
        var names = new List<string>();
        try
        {
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(DisplayAdapterClassKey);
            if (classKey is null)
            {
                return names;
            }

            foreach (string subkeyName in classKey.GetSubKeyNames())
            {
                using RegistryKey? adapterKey = classKey.OpenSubKey(subkeyName);
                if (adapterKey?.GetValue("DriverDesc") is string description
                    && !string.IsNullOrWhiteSpace(description))
                {
                    names.Add(description.Trim());
                }
            }
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException)
        {
            // Unreadable registry: treat as no detectable NVIDIA adapter rather than guessing.
        }

        return names;
    }
}
