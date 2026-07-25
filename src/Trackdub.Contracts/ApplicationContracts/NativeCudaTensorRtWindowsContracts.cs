namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>
/// When enabled, Windows may use native ONNX Runtime CUDA and TensorRT execution providers
/// instead of mapping CUDA to DirectML or native TensorRT to the TensorRT RTX plugin route.
/// Default is off; pipeline auto-selection is unchanged.
/// </summary>
public interface INativeCudaTensorRtWindowsPolicy
{
    Task<bool> IsNativeProvidersAllowedOnWindowsAsync(CancellationToken cancellationToken = default);
}

public interface INativeCudaTensorRtWindowsReadinessService
{
    Task<NativeCudaTensorRtWindowsReadinessSnapshot> ProbeAsync(CancellationToken cancellationToken = default);
}

public sealed record NativeCudaTensorRtWindowsReadinessSnapshot(
    bool PanelVisible,
    bool IsSettingEnabled,
    string CudaStatusLabel,
    string CudaDetail,
    string TensorRtStatusLabel,
    string TensorRtDetail,
    string? SettingHint,
    string? CudaInstallHint,
    string? TensorRtInstallHint);

public interface INativeCudaTensorRtWindowsReadinessProbe
{
    Task<NativeCudaTensorRtWindowsReadinessReport> ProbeAsync(
        bool isSettingEnabled,
        CancellationToken cancellationToken = default);
}

public sealed record NativeCudaTensorRtWindowsReadinessReport(
    bool IsSupportedPlatform,
    bool IsSettingEnabled,
    bool IsHardwareEligible,
    bool IsCudaOrtProviderListed,
    bool IsNativeTensorRtLibrariesPresent,
    bool IsNativeTensorRtOrtProviderListed,
    bool IsCudaReady,
    bool IsTensorRtReady,
    string CudaDetail,
    string TensorRtDetail,
    string? CudaInstallHint,
    string? TensorRtInstallHint);
