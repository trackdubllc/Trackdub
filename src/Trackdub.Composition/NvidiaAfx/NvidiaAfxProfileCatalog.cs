using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

/// <summary>
/// One Maxine feature-folder model requirement. Resolved on disk via
/// <see cref="Infrastructure.Components.NvidiaAfx.NvidiaAfxRuntimeLayout"/>.
/// </summary>
public sealed record NvidiaAfxRequiredModel(
    string FeatureFolder,
    string ModelStem);

public sealed record NvidiaAfxProfileDefinition(
    NvidiaAfxProfile Profile,
    string DisplayName,
    string Selector,
    bool IsChainedEffect,
    /// <summary>Supported input sample rates for this effect.</summary>
    int[] SupportedSampleRates,
    int MaxChannels,
    NvidiaAfxRequiredModel[] RequiredModels,
    bool RequiresFarEndReference,
    bool SupportsIntensityRatio,
    /// <summary>
    /// Explicit output sample rate when it differs from the selected input rate
    /// (for example telephony upscale 8 kHz → 16 kHz). Null means output matches input.
    /// </summary>
    int? OutputSampleRate = null)
{
    public int ResolveOutputSampleRate(int inputSampleRate) =>
        OutputSampleRate ?? inputSampleRate;

    /// <summary>
    /// Preferred relative paths for manifests / diagnostics (Maxine 3.x features layout).
    /// Actual resolution accepts additional arch folders and extensions.
    /// </summary>
    public IReadOnlyList<string> PreferredModelRelativePaths =>
        RequiredModels
            .Select(model => Path.Join("features", model.FeatureFolder, "models", model.ModelStem + ".trtpkg"))
            .ToArray();
}

public static class NvidiaAfxProfileCatalog
{
    /// <summary>
    /// First-class discovery surface for AFX profiles. Profiles are listed even while the
    /// integration is stubbed so planners/settings can enumerate them without implying readiness.
    /// </summary>
    public static IReadOnlyList<NvidiaAfxProfileDefinition> Definitions { get; } =
    [
        new(
            NvidiaAfxProfile.NoiseOnly,
            "Noise Removal",
            Selector: "denoiser",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModels:
            [
                new("nvafxdenoiser", "denoiser_48k"),
            ],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.ReverbOnly,
            "Reverb Removal",
            Selector: "dereverb",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModels:
            [
                new("nvafxdereverb", "dereverb_48k"),
            ],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.NoiseAndReverb,
            "Noise + Reverb Removal",
            Selector: "dereverb_denoiser",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModels:
            [
                new("nvafxdereverbdenoiser", "dereverb_denoiser_48k"),
            ],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.TelephonyUpscale,
            "Telephony Upscale",
            // Matches NVIDIA Maxine chained selector (8 kHz → 16 kHz + denoise).
            Selector: "superres8kto16k_denoiser16k",
            IsChainedEffect: true,
            SupportedSampleRates: [8000],
            MaxChannels: 1,
            RequiredModels:
            [
                new("nvafxsuperres", "superres_8k_to_16k"),
                new("nvafxdenoiser", "denoiser_16k"),
            ],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true,
            OutputSampleRate: 16000),
        new(
            NvidiaAfxProfile.AcousticEchoCancellation,
            "Acoustic Echo Cancellation",
            // Not listed in Maxine AFX 3.x public effect selectors; kept for discovery.
            // Readiness/model resolution will fail until NVIDIA ships a matching feature package.
            Selector: "aec",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModels:
            [
                new("nvafxaec", "aec_48k"),
            ],
            RequiresFarEndReference: true,
            SupportsIntensityRatio: false)
    ];

    public static NvidiaAfxProfileDefinition GetDefinition(NvidiaAfxProfile profile) =>
        Definitions.FirstOrDefault(definition => definition.Profile == profile)
        ?? Definitions.First(definition => definition.Profile == NvidiaAfxProfile.NoiseAndReverb);
}
