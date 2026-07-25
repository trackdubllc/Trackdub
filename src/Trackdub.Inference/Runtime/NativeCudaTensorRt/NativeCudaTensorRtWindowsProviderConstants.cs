namespace Trackdub.Inference.Runtime.NativeCudaTensorRt;

public static class NativeCudaTensorRtWindowsProviderConstants
{
    public const string CudaOrtExecutionProviderName = "CUDAExecutionProvider";

    public const string WindowsCudaInstallHint =
        "Install NVIDIA drivers, CUDA 12 runtime, and cuDNN 9 (cudnn64_9.dll), then enable 'Allow native CUDA/TensorRT' in Settings → Hardware.";

    public const string WindowsCuDnn9InstallHint =
        "Install cuDNN 9 for CUDA 12: pip install nvidia-cudnn-cu12 and add the bin/ directory to PATH, or install the NVIDIA cuDNN 9 package.";

    public const string WindowsTensorRtInstallHint =
        "Install NVIDIA drivers and TensorRT (nvinfer.dll on PATH), then verify ONNX Runtime lists TensorrtExecutionProvider.";

    public const string SettingDisabledHint =
        "Enable “Allow native CUDA / TensorRT on Windows” in Settings → Hardware to probe native ORT providers (advanced).";
}
