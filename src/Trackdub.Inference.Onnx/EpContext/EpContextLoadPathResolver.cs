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
    private static readonly Lazy<(string GpuArchitecture, string? DriverVersion)> Hardware = new(ProbeHardware);

    public static string CurrentEnvironmentFingerprint =>
        $"{CurrentHardware.GpuArchitecture}|{(string.IsNullOrWhiteSpace(CurrentHardware.DriverVersion) ? "unknown" : CurrentHardware.DriverVersion)}|{TensorRtRtxProviderConstants.BundledFingerprintVersion}";

    /// <summary>GPU architecture and driver the fingerprint (and new stamps) are built from.</summary>
    public static (string GpuArchitecture, string? DriverVersion) CurrentHardware => Hardware.Value;

    /// <summary>Valid EP-context sibling for <paramref name="sourceModelPath"/>, or <see langword="null"/>.</summary>
    public static string? TryResolveLoadPath(string sourceModelPath) =>
        EpContextArtifact.TryResolveValidLoadPath(sourceModelPath, CurrentEnvironmentFingerprint);

    private static (string GpuArchitecture, string? DriverVersion) ProbeHardware()
    {
        try
        {
            HardwareProfile hardware = new MachineHardwareProfileProvider().GetCurrentAsync().GetAwaiter().GetResult();
            return (hardware.NvidiaGpuArchitecture.ToString(), hardware.GpuDriverVersion);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            return ("unknown", null);
        }
    }
}
