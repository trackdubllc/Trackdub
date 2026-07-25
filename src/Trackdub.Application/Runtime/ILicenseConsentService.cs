namespace Trackdub.Application.Runtime;

/// <summary>
/// License family for a Windows ML catalog execution provider.
/// One flag per vendor/family — not per individual EP — so AMD is checked once
/// regardless of whether MIGraphX, VitisAI, or both are being installed.
/// </summary>
public enum EpVendorLicense
{
    /// <summary>AMD — covers MIGraphX (GPU) and VitisAI (XDNA NPU).</summary>
    AmdRyzenAi,

    /// <summary>NVIDIA — covers NvTensorRtRtx.</summary>
    NvidiaTensorRtRtx,

    /// <summary>Intel — covers OpenVINO.</summary>
    IntelOpenVino,

    /// <summary>Qualcomm — covers QNN.</summary>
    QualcommQnn,
}

/// <summary>
/// Surfaces vendor license terms before a Windows ML catalog EP install and
/// persists one-time per-machine acceptance per license family.
/// </summary>
public interface ILicenseConsentService
{
    /// <summary>
    /// Returns <c>true</c> immediately if the license is already accepted.
    /// Otherwise shows a modal consent dialog; returns <c>true</c> only on
    /// explicit acceptance, <c>false</c> on cancel or dismiss.
    /// </summary>
    Task<bool> EnsureAcceptedAsync(EpVendorLicense license, CancellationToken cancellationToken);

    /// <summary>
    /// Opens the vendor license dialog in informational mode without modifying
    /// the stored acceptance flag. Used by "View license" links on provider cards.
    /// </summary>
    Task ShowLicenseAsync(EpVendorLicense license, CancellationToken cancellationToken);
}
