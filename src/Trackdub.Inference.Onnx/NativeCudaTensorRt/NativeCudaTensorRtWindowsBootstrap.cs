using Trackdub.Domain;
using Trackdub.Inference.Onnx.ExecutionProviders;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Onnx.TensorRtRtx;
using Trackdub.Inference.Runtime.NativeCudaTensorRt;

namespace Trackdub.Inference.Onnx.NativeCudaTensorRt;

internal static class NativeCudaTensorRtWindowsBootstrap
{
    public static ExecutionProviderBootstrapResult Bootstrap(
        ExecutionProviderKind provider,
        bool allowDownloads)
    {
        _ = allowDownloads;

        (bool hardwareEligible, _, string hardwareDetail) = WindowsNvidiaHardwareGate.Evaluate();
        if (!hardwareEligible)
        {
            return FailClosed(provider, hardwareDetail);
        }

        return provider switch
        {
            ExecutionProviderKind.Cuda => BootstrapCuda(),
            ExecutionProviderKind.TensorRt => BootstrapTensorRt(),
            _ => FailClosed(provider, $"Provider {provider} is not a native Windows ORT GPU provider.")
        };
    }

    private static ExecutionProviderBootstrapResult BootstrapCuda()
    {
        if (!CudaOrtProbe.IsCudaProviderListed())
        {
            return FailClosed(
                ExecutionProviderKind.Cuda,
                "CUDAExecutionProvider is not listed. "
                + NativeCudaTensorRtWindowsProviderConstants.WindowsCudaInstallHint);
        }

        if (!CudaOrtProbe.IsCuDnn9Available())
        {
            return FailClosed(
                ExecutionProviderKind.Cuda,
                "cuDNN 9 (cudnn64_9.dll) not found. "
                + NativeCudaTensorRtWindowsProviderConstants.WindowsCuDnn9InstallHint);
        }

        return new ExecutionProviderBootstrapResult(
            ExecutionProviderKind.Cuda,
            ExecutionProviderKind.Cuda,
            Succeeded: true,
            Detail: "Native ORT CUDA execution provider and cuDNN 9 are available.");
    }

    private static ExecutionProviderBootstrapResult BootstrapTensorRt()
    {
        bool librariesPresent = NativeTensorRtLibraryProbe.IsNativeTensorRtAvailable();
        bool ortListed = TensorRtRtxOrtProbe.IsNativeTensorRtProviderListed();

        if (!librariesPresent || !ortListed)
        {
            string detail = !librariesPresent
                ? "nvinfer.dll was not found. " + NativeCudaTensorRtWindowsProviderConstants.WindowsTensorRtInstallHint
                : "TensorrtExecutionProvider is not listed. "
                  + NativeCudaTensorRtWindowsProviderConstants.WindowsTensorRtInstallHint;
            return FailClosed(ExecutionProviderKind.TensorRt, detail);
        }

        return new ExecutionProviderBootstrapResult(
            ExecutionProviderKind.TensorRt,
            ExecutionProviderKind.TensorRt,
            Succeeded: true,
            Detail: "Native ORT TensorRT execution provider libraries and EP are available.");
    }

    private static ExecutionProviderBootstrapResult FailClosed(ExecutionProviderKind provider, string detail) =>
        new(
            provider,
            provider,
            Succeeded: false,
            Detail: detail,
            FailureReason: detail);
}
