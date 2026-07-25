using System.Runtime.Versioning;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Inference.Runtime.WinMlCatalog;

namespace Trackdub.Inference.Onnx.WinMlCatalog;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsIntelCatalogHardwareGate
{
    public static (bool Eligible, WinMlCatalogReadinessBlocker Blocker, string Detail) Evaluate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return (false, WinMlCatalogReadinessBlocker.PlatformUnsupported, "OpenVINO catalog route is Windows-only.");
        }

        string? processorName = WindowsCatalogHardwareProbe.TryReadProcessorName();
        int? processorGeneration = IntelCatalogHardwareGenerationParser.TryParseProcessorGeneration(
            processorName,
            out int parsedGeneration)
            ? parsedGeneration
            : null;

        bool cpuPath = processorGeneration >= IntelCatalogHardwareGenerationParser.MinCpuGeneration;
        bool gpuPath = HasQualifyingIntelGpu(processorGeneration);
        bool npuPath = HasQualifyingIntelNpu(processorName, processorGeneration);

        if (cpuPath || gpuPath || npuPath)
        {
            string path = cpuPath ? "CPU" : gpuPath ? "GPU" : "NPU";
            return (
                true,
                WinMlCatalogReadinessBlocker.None,
                $"Intel {path} hardware meets Windows ML OpenVINO catalog requirements.");
        }

        return (
            false,
            WinMlCatalogReadinessBlocker.HardwareNotSupported,
            OpenVinoCatalogProviderConstants.HardwareNotSupportedDetail);
    }

    private static bool HasQualifyingIntelGpu(int? processorGeneration)
    {
        bool found = false;
        WindowsCatalogHardwareProbe.ForEachIntelDisplayAdapter((driverDesc, deviceDesc) =>
        {
            if (found)
            {
                return;
            }

            string combined = $"{driverDesc} {deviceDesc}";
            if (IntelCatalogHardwareGenerationParser.IsIntelArcGraphics(combined))
            {
                found = true;
                return;
            }

            if (IntelCatalogHardwareGenerationParser.TryParseGenerationFromDescription(combined, out int gen) &&
                gen >= IntelCatalogHardwareGenerationParser.MinGpuGeneration)
            {
                found = true;
                return;
            }

            if (processorGeneration >= IntelCatalogHardwareGenerationParser.MinGpuGeneration &&
                IntelCatalogHardwareGenerationParser.IsIntelIntegratedGraphics(combined))
            {
                found = true;
            }
        });

        return found;
    }

    private static bool HasQualifyingIntelNpu(string? processorName, int? processorGeneration)
    {
        if (!WindowsCatalogHardwareProbe.HasIntelNpuDevice())
        {
            return false;
        }

        if (processorGeneration >= IntelCatalogHardwareGenerationParser.MinNpuGeneration)
        {
            return true;
        }

        return IntelCatalogHardwareGenerationParser.IsCoreUltraSeries2(processorName);
    }
}
