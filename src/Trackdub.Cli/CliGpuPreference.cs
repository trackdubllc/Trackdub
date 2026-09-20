using System.CommandLine;
using System.CommandLine.Parsing;
using Microsoft.Win32;

using Trackdub.Domain;

namespace Trackdub.Cli;

/// <summary>
/// Resolves <c>--prefer-gpu</c> / <c>--require-gpu</c> into an execution-provider pin.
/// Order: detected vendor GPU EP first, then DirectML. The runtime planner still
/// falls through vendor → … → DirectML → CPU per stage allow-lists when the
/// preference is soft (default) or a stage forbids the preferred EP.
/// </summary>
internal static class CliGpuPreference
{
    internal enum GpuVendor
    {
        Unknown = 0,
        Nvidia,
        Amd,
        Intel,
    }

    /// <summary>
    /// Explicit <c>--execution-provider</c> (non-auto) always wins over --prefer-gpu.
    /// <c>--require-gpu</c> sets require=true; with an explicit EP that EP is hard-pinned.
    /// </summary>
    internal static (string? ExecutionProvider, bool Require, int? ArgumentErrorExitCode) Apply(
        ParseResult parseResult,
        string? executionProvider,
        bool requireExecutionProvider)
    {
        bool preferGpu = CliParseHelpers.GetGlobalOptionValue<bool>(parseResult, "prefer-gpu");
        bool requireGpu = CliParseHelpers.GetGlobalOptionValue<bool>(parseResult, "require-gpu");
        return Apply(executionProvider, requireExecutionProvider, preferGpu, requireGpu);
    }

    internal static (string? ExecutionProvider, bool Require, int? ArgumentErrorExitCode) Apply(
        string? executionProvider,
        bool requireExecutionProvider,
        bool preferGpu,
        bool requireGpu)
    {
        if (!preferGpu && !requireGpu)
        {
            return (executionProvider, requireExecutionProvider, null);
        }

        bool epPinned = !string.IsNullOrWhiteSpace(executionProvider)
            && !executionProvider.Trim().Equals("auto", StringComparison.OrdinalIgnoreCase);

        if (requireGpu && epPinned
            && executionProvider!.Trim().Equals("cpu", StringComparison.OrdinalIgnoreCase))
        {
            return (executionProvider, requireExecutionProvider, Program.ExitArgumentError);
        }

        if (epPinned)
        {
            // Explicit pin wins; --require-gpu still hard-requires that pin when requested.
            return (executionProvider, requireExecutionProvider || requireGpu, null);
        }

        ExecutionProviderKind preferred = ResolvePreferredGpuKind();
        string token = ExecutionProviderTokens.ToCanonicalTag(preferred);
        return (token, requireGpu, null);
    }

    /// <summary>
    /// Vendor GPU EP first, then DirectML as the universal Windows GPU fallback.
    /// </summary>
    internal static ExecutionProviderKind ResolvePreferredGpuKind()
    {
        GpuVendor vendor = DetectVendor();
        return vendor switch
        {
            GpuVendor.Nvidia when OperatingSystem.IsWindows() => ExecutionProviderKind.TensorRTRtx,
            GpuVendor.Nvidia => ExecutionProviderKind.Cuda,
            GpuVendor.Amd => ExecutionProviderKind.Migraphx,
            GpuVendor.Intel when OperatingSystem.IsWindows() => ExecutionProviderKind.OpenVinoCatalog,
            GpuVendor.Intel => ExecutionProviderKind.OpenVino,
            _ => ExecutionProviderKind.DirectMl,
        };
    }

    internal static GpuVendor DetectVendor()
    {
        if (OperatingSystem.IsWindows())
        {
            return DetectWindowsDisplayVendor();
        }

        return GpuVendor.Unknown;
    }

    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    private static GpuVendor DetectWindowsDisplayVendor()
    {
        try
        {
            const string displayClassGuid =
                @"SYSTEM\CurrentControlSet\Control\Class\{4d36e968-e325-11ce-bfc1-08002be10318}";
            using RegistryKey? classKey = Registry.LocalMachine.OpenSubKey(displayClassGuid);
            if (classKey is null)
            {
                return GpuVendor.Unknown;
            }

            foreach (string subKeyName in classKey.GetSubKeyNames())
            {
                if (!int.TryParse(subKeyName, out _))
                {
                    continue;
                }

                using RegistryKey? deviceKey = classKey.OpenSubKey(subKeyName);
                if (deviceKey is null)
                {
                    continue;
                }

                string? provider = deviceKey.GetValue("ProviderName") as string;
                string? desc = deviceKey.GetValue("DriverDesc") as string;
                string haystack = $"{provider} {desc}";
                if (haystack.Contains("NVIDIA", StringComparison.OrdinalIgnoreCase))
                {
                    return GpuVendor.Nvidia;
                }

                if (haystack.Contains("AMD", StringComparison.OrdinalIgnoreCase)
                    || haystack.Contains("Radeon", StringComparison.OrdinalIgnoreCase)
                    || haystack.Contains("Advanced Micro Devices", StringComparison.OrdinalIgnoreCase))
                {
                    return GpuVendor.Amd;
                }

                if (haystack.Contains("Intel", StringComparison.OrdinalIgnoreCase))
                {
                    return GpuVendor.Intel;
                }
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            return GpuVendor.Unknown;
        }

        return GpuVendor.Unknown;
    }
}
