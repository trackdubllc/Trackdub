namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>Stable provider identifiers for manifest and readiness reporting.</summary>
public static class TensorRtRtxProviderIds
{
    public const string PluginEpAbi = "tensorrt-rtx-plugin-ep-abi";
    public const string Native = "tensorrt-native";
}

public enum TensorRtRtxReadinessBlocker
{
    None = 0,
    GpuVendorMismatch,
    OsVersionUnsupported,
    EpNotPresent,
    EpNotReady,
    EpDownloadFailed,
    /// <summary>CUDA 12 runtime (cudart64_12 / libcudart.so.12) is missing or failed to load. Not the same as plugin-not-present.</summary>
    CudaRuntimeMissing,
    EpRegisterFailed,
    OrtProviderUnavailable,
    PlatformUnsupported
}

public enum TensorRtRtxPlatformRoute
{
    None = 0,
    PluginEpAbi,
    NativeTensorRt
}

public sealed record TensorRtRtxReadinessReport(
    string ProviderId,
    TensorRtRtxPlatformRoute Route,
    TensorRtRtxReadinessBlocker Blocker,
    bool IsHardwareEligible,
    bool IsOrtProviderListed,
    bool IsRegisteredWithOrt,
    string Detail)
{
    public bool IsReady =>
        Blocker == TensorRtRtxReadinessBlocker.None &&
        IsHardwareEligible &&
        IsOrtProviderListed &&
        IsRegisteredWithOrt;
}

public interface ITensorRtRtxReadinessProbe
{
    Task<TensorRtRtxReadinessReport> ProbeAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public interface ITensorRtRtxProviderBootstrap
{
    Task<TensorRtRtxBootstrapResult> EnsureRegisteredAsync(
        bool allowProviderDownloads,
        CancellationToken cancellationToken = default);
}

public sealed record TensorRtRtxBootstrapResult(
    bool Succeeded,
    string? ProviderId,
    TensorRtRtxReadinessBlocker? Blocker,
    string Detail);
