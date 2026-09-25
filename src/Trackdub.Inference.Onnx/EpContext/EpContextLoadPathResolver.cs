using System.Diagnostics;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Runtime.Planning;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.Inference.Runtime.TensorRtRtx;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.EpContext;

/// <summary>
/// Cached machine fingerprint + optional valid EP-context load path for session creation.
/// Fingerprints are expensive (registry/GPU probe), so they are computed once per process.
/// </summary>
internal static class EpContextLoadPathResolver
{
    private static readonly object Gate = new();
    private static string? _environmentFingerprint;

    public static string CurrentEnvironmentFingerprint
    {
        get
        {
            if (_environmentFingerprint is not null)
            {
                return _environmentFingerprint;
            }

            lock (Gate)
            {
                if (_environmentFingerprint is null)
                {
                    _environmentFingerprint = BuildFingerprint();
                }
            }

            return _environmentFingerprint;
        }
    }

    /// <summary>Valid EP-context sibling for <paramref name="sourceModelPath"/>, or <see langword="null"/>.</summary>
    public static string? TryResolveLoadPath(string sourceModelPath) =>
        EpContextArtifact.TryResolveValidLoadPath(sourceModelPath, CurrentEnvironmentFingerprint);

    private static string BuildFingerprint()
    {
        try
        {
            HardwareProfile hardware = new MachineHardwareProfileProvider().GetCurrentAsync().GetAwaiter().GetResult();
            string driver = string.IsNullOrWhiteSpace(hardware.GpuDriverVersion) ? "unknown" : hardware.GpuDriverVersion;
            return $"{hardware.NvidiaGpuArchitecture}|{driver}|{TensorRtRtxProviderConstants.BundledFingerprintVersion}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return $"unknown|unknown|{TensorRtRtxProviderConstants.BundledFingerprintVersion}";
        }
    }
}
