namespace Trackdub.Contracts;

/// <summary>
/// Identifies which NVIDIA RTXVoice / AFX processing profile to apply
/// during speech audio enhancement.
/// </summary>
public enum NvidiaAfxProfile
{
    /// <summary>Noise suppression only (denoiser).</summary>
    NoiseOnly = 0,

    /// <summary>Reverb removal only (dereverb).</summary>
    ReverbOnly = 1,

    /// <summary>Combined noise and reverb removal (dereverb_denoiser). Default.</summary>
    NoiseAndReverb = 2,

    /// <summary>Telephony upscale with combined denoiser (superres_denoiser).</summary>
    TelephonyUpscale = 3,

    /// <summary>Acoustic echo cancellation (aec). Requires far-end reference audio.</summary>
    AcousticEchoCancellation = 4,
}
