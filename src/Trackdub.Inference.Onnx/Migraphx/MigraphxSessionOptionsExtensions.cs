using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Migraphx;

namespace Trackdub.Inference.Onnx.Migraphx;

internal static class MigraphxSessionOptionsExtensions
{
    public static bool TryAppendMigraphxProvider(SessionOptions options, out string? failureReason)
    {
        failureReason = null;

#if WINDOWS
        Trackdub.Inference.Onnx.WindowsMl.WindowsMlOnnxRuntimeNativeResolver.EnsureInitialized();
#endif

        try
        {
            OrtEpDevice? device = OrtEnv.Instance()
                .GetEpDevices()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.EpName, MigraphxProviderConstants.OrtExecutionProviderName, StringComparison.OrdinalIgnoreCase) &&
                    candidate.HardwareDevice.Type is OrtHardwareDeviceType.GPU);

            if (device is not null)
            {
                var providerOptions = new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["device_id"] = "0"
                };
                options.AppendExecutionProvider(OrtEnv.Instance(), new[] { device }, providerOptions);
                return true;
            }

            if (MigraphxOrtProbe.IsProviderListed())
            {
                options.AppendExecutionProvider(
                    MigraphxProviderConstants.OrtExecutionProviderName,
                    new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["device_id"] = "0"
                    });
                return true;
            }

            failureReason = $"{MigraphxProviderConstants.OrtExecutionProviderName} is not registered with ONNX Runtime.";
            return false;
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or InvalidOperationException or DllNotFoundException or EntryPointNotFoundException)
        {
            failureReason = ex.Message;
            return false;
        }
    }

    public static ExecutionProviderKind AppendMigraphxOrFallback(SessionOptions options)
    {
        return TryAppendMigraphxProvider(options, out _)
            ? ExecutionProviderKind.Migraphx
            : ExecutionProviderKind.Cpu;
    }
}
