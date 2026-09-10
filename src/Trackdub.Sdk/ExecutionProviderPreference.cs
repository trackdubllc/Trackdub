using Trackdub.Domain;

namespace Trackdub.Sdk;

/// <summary>
/// Legacy four-value preference kept for source compatibility.
/// Prefer <see cref="TrackdubBuilder.WithExecutionProvider(ExecutionProviderKind)"/>
/// or <c>null</c> (auto) via <see cref="TrackdubOptions.PreferredExecutionProvider"/>.
/// </summary>
public enum ExecutionProviderPreference
{
    /// <summary>Let the runtime planner choose the best available provider.</summary>
    Auto = 0,

    /// <summary>Force CPU-only inference.</summary>
    Cpu = 1,

    /// <summary>Request legacy Windows GPU acceleration via DirectML (Windows ML packaged route).</summary>
    DirectML = 2,

    /// <summary>
    /// NVIDIA acceleration preference. On Windows maps to TensorRT RTX (<c>trt-rtx</c>);
    /// on Linux maps to native CUDA.
    /// </summary>
    Cuda = 3
}

internal static class ExecutionProviderPreferenceMapping
{
    public static ExecutionProviderKind? ToPreferredKind(ExecutionProviderPreference preference) =>
        preference switch
        {
            ExecutionProviderPreference.Auto => null,
            ExecutionProviderPreference.Cpu => ExecutionProviderKind.Cpu,
            ExecutionProviderPreference.DirectML => ExecutionProviderKind.DirectMl,
            ExecutionProviderPreference.Cuda => OperatingSystem.IsWindows()
                ? ExecutionProviderKind.TensorRTRtx
                : ExecutionProviderKind.Cuda,
            _ => throw new ArgumentOutOfRangeException(
                nameof(preference),
                preference,
                "Unknown execution provider preference."),
        };

    public static ExecutionProviderPreference ToLegacyPreference(ExecutionProviderKind? kind) =>
        kind switch
        {
            null => ExecutionProviderPreference.Auto,
            ExecutionProviderKind.Cpu => ExecutionProviderPreference.Cpu,
            ExecutionProviderKind.DirectMl => ExecutionProviderPreference.DirectML,
            ExecutionProviderKind.Cuda or ExecutionProviderKind.TensorRTRtx => ExecutionProviderPreference.Cuda,
            _ => ExecutionProviderPreference.Auto,
        };
}
