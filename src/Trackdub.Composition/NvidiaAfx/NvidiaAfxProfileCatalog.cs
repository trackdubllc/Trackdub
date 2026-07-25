using Trackdub.Contracts;

namespace Trackdub.Composition.NvidiaAfx;

public sealed record NvidiaAfxProfileDefinition(
    NvidiaAfxProfile Profile,
    string DisplayName,
    string Selector,
    bool IsChainedEffect,
    int[] SupportedSampleRates,
    int MaxChannels,
    string[] RequiredModelRelativePaths,
    bool RequiresFarEndReference,
    bool SupportsIntensityRatio);

public static class NvidiaAfxProfileCatalog
{
    public static IReadOnlyList<NvidiaAfxProfileDefinition> Definitions { get; } =
    [
        new(
            NvidiaAfxProfile.NoiseOnly,
            "Noise Removal",
            Selector: "denoiser",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModelRelativePaths: ["models/denoiser_48k.nvam"],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.ReverbOnly,
            "Reverb Removal",
            Selector: "dereverb",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModelRelativePaths: ["models/dereverb_48k.nvam"],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.NoiseAndReverb,
            "Noise + Reverb Removal",
            Selector: "dereverb_denoiser",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModelRelativePaths: ["models/dereverb_denoiser_48k.nvam"],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.TelephonyUpscale,
            "Telephony Upscale",
            Selector: "superres_denoiser",
            IsChainedEffect: true,
            SupportedSampleRates: [16000],
            MaxChannels: 1,
            RequiredModelRelativePaths: ["models/superres_48k.nvam", "models/denoiser_48k.nvam"],
            RequiresFarEndReference: false,
            SupportsIntensityRatio: true),
        new(
            NvidiaAfxProfile.AcousticEchoCancellation,
            "Acoustic Echo Cancellation",
            Selector: "aec",
            IsChainedEffect: false,
            SupportedSampleRates: [16000, 48000],
            MaxChannels: 1,
            RequiredModelRelativePaths: ["models/aec_48k.nvam"],
            RequiresFarEndReference: true,
            SupportsIntensityRatio: false)
    ];

    public static NvidiaAfxProfileDefinition GetDefinition(NvidiaAfxProfile profile) =>
        Definitions.FirstOrDefault(definition => definition.Profile == profile)
        ?? Definitions.First(definition => definition.Profile == NvidiaAfxProfile.NoiseAndReverb);
}
