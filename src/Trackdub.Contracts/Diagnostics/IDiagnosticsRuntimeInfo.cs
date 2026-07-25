namespace Trackdub.Contracts.Diagnostics;

/// <summary>
/// Provides runtime and hardware information for diagnostics collection.
/// Implementations may query the ONNX runtime or OS-level hardware APIs.
/// </summary>
public interface IDiagnosticsRuntimeInfo
{
    /// <summary>Gets a human-readable GPU description, or <see langword="null"/> if unavailable.</summary>
    string? GpuDescription { get; }

    /// <summary>Gets whether DirectML execution provider is available on this machine.</summary>
    bool DirectMlAvailable { get; }

    /// <summary>Gets the ONNX Runtime package version string, or <see langword="null"/> if unavailable.</summary>
    string? OnnxRuntimeVersion { get; }

    /// <summary>Gets the Windows App SDK version string, or <see langword="null"/> if unavailable.</summary>
    string? WindowsAppSdkVersion { get; }

    /// <summary>Gets whether MIGraphX is ready for ONNX Runtime sessions on this machine.</summary>
    bool MigraphxAvailable { get; }

    /// <summary>Human-readable MIGraphX readiness detail (install hints when unavailable).</summary>
    string? MigraphxReadinessDetail { get; }
}
