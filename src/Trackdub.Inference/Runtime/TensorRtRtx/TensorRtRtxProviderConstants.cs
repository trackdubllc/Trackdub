using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Runtime.TensorRtRtx;

public static class TensorRtRtxProviderConstants
{
    public const string PluginOrtExecutionProviderName = "NvTensorRTRTXExecutionProvider";

    public const string PluginLibraryFileNameWindows = "onnxruntime_providers_nv_tensorrt_rtx.dll";

    public const string PluginLibraryFileNameLinux = "libonnxruntime_providers_nv_tensorrt_rtx.so";

    public const string TensorRtRuntimeFileNameWindows = "tensorrt_rtx_1_5.dll";

    public const string TensorRtRuntimeFileNameLinux = "libtensorrt_rtx.so";

    public const string TensorRtOnnxParserFileNameWindows = "tensorrt_onnxparser_rtx_1_5.dll";

    public const string TensorRtOnnxParserFileNameLinux = "libtensorrt_onnxparser_rtx.so";

    public const string PluginDirectoryEnvironmentVariable = "TRACKDUB_TRT_RTX_EP_DIR";

    public const string CudaRuntimeBinDirectoryEnvironmentVariable = "TRACKDUB_CUDA12_BIN_DIR";

    public const string BundledVersion = "0.3.0";

    public const string BundledCudaVariant = "cu12";

    /// <summary>
    /// Version identity for anything that must invalidate with the engine cache (smoke verdicts,
    /// EP-context stamps): changes when the bundled EP ABI plugin changes.
    /// </summary>
    public static string BundledFingerprintVersion => BundledVersion;

    public const string NativeOrtExecutionProviderName = "TensorrtExecutionProvider";

    public static string ProviderIdPluginEpAbi => TensorRtRtxProviderIds.PluginEpAbi;

    public static string ProviderIdNative => TensorRtRtxProviderIds.Native;

    public static string PluginLibraryFileName =>
        OperatingSystem.IsLinux()
            ? PluginLibraryFileNameLinux
            : PluginLibraryFileNameWindows;

    public static string TensorRtRuntimeFileName =>
        OperatingSystem.IsLinux()
            ? TensorRtRuntimeFileNameLinux
            : TensorRtRuntimeFileNameWindows;

    public static string TensorRtOnnxParserFileName =>
        OperatingSystem.IsLinux()
            ? TensorRtOnnxParserFileNameLinux
            : TensorRtOnnxParserFileNameWindows;

    public static IReadOnlyList<string> RequiredPluginFileNames =>
    [
        PluginLibraryFileName,
        TensorRtRuntimeFileName,
        TensorRtOnnxParserFileName
    ];

    public const string LinuxInstallHint =
        "Use Install in Model Manager to download TensorRT-RTX-EP-ABI v0.3.0 cu12 for linux-x64, or run tools/dev/Fetch-TrtRtxEp.ps1, then refresh readiness.";

    public const string WindowsInstallHint =
        "Use Install in Model Manager to download TensorRT-RTX-EP-ABI v0.3.0 cu12 for win-x64, or run tools/dev/Fetch-TrtRtxEp.ps1, then refresh readiness. "
        + "CUDA 12 runtime (cudart64_12.dll) must also be available via CUDA Toolkit 12.x, `pip install nvidia-cuda-runtime-cu12`, or TRACKDUB_CUDA12_BIN_DIR.";

    public static string GetDefaultInstallDirectory(string userDataRoot, string runtimeIdentifier) =>
        Path.Combine(
            Path.GetFullPath(Environment.ExpandEnvironmentVariables(userDataRoot)),
            "Providers",
            "trt-rtx",
            BundledVersion,
            BundledCudaVariant,
            runtimeIdentifier);
}
