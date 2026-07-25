using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;

namespace Trackdub.Inference.Onnx.Dnnl;

internal static class DnnlSessionOptionsExtensions
{
    public static bool TryAppendDnnlProvider(SessionOptions options, out string? failureReason)
    {
        try
        {
            options.AppendExecutionProvider_Dnnl(useArena: 1);
            failureReason = null;
            return true;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException
            or InvalidOperationException
            or DllNotFoundException
            or EntryPointNotFoundException
            or BadImageFormatException
            or FileLoadException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    public static ExecutionProviderKind AppendDnnlOrFallback(SessionOptions options, out string? failureReason) =>
        TryAppendDnnlProvider(options, out failureReason)
            ? ExecutionProviderKind.Dnnl
            : ExecutionProviderKind.Cpu;
}
