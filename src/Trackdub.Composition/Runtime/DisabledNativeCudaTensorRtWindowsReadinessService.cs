using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Composition.Runtime;

/// <summary>
/// Non-Windows TFMs: native CUDA/TensorRT on Windows is not applicable.
/// </summary>
public sealed class DisabledNativeCudaTensorRtWindowsReadinessService : INativeCudaTensorRtWindowsReadinessService
{
    public Task<NativeCudaTensorRtWindowsReadinessSnapshot> ProbeAsync(CancellationToken cancellationToken = default) =>
        Task.FromResult(new NativeCudaTensorRtWindowsReadinessSnapshot(
            PanelVisible: false,
            IsSettingEnabled: false,
            CudaStatusLabel: "Unavailable",
            CudaDetail: "Native CUDA / TensorRT on Windows is only available on the Windows app target.",
            TensorRtStatusLabel: "Unavailable",
            TensorRtDetail: "Native CUDA / TensorRT on Windows is only available on the Windows app target.",
            SettingHint: null,
            CudaInstallHint: null,
            TensorRtInstallHint: null));
}
