using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Runtime.Migraphx;

public static class MigraphxProviderConstants
{
    public const string OrtExecutionProviderName = "MIGraphXExecutionProvider";

    public static string ProviderIdWinMl => MigraphxProviderIds.WinMl;

    public static string ProviderIdRocm => MigraphxProviderIds.Rocm;

    /// <summary>Windows 11 24H2 minimum build per Windows ML MIGraphX documentation.</summary>
    public const int WindowsMinimumBuild = 26100;

    /// <summary>AMD GPU driver version required for Windows ML MIGraphX EP (exact match policy).</summary>
    public const string WindowsRequiredAmdDriverVersion = "25.10.13.09";

    public const string LinuxInstallHint =
        "Install ROCm and MIGraphX, then an ONNX Runtime build that exposes MIGraphXExecutionProvider " +
        "(see AMD install-onnx documentation). Verify with: python3 -c \"import onnxruntime as ort; print(ort.get_available_providers())\".";
}
