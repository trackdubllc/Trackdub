using Trackdub.Contracts;
using Trackdub.Composition.NvidiaAfx;

namespace Trackdub.Composition.Tests;

public sealed class NvidiaAfxProfileCatalogTests
{
    [Fact]
    public void Definitions_ContainAllShippingProfiles()
    {
        var expected = new[]
        {
            NvidiaAfxProfile.NoiseOnly,
            NvidiaAfxProfile.ReverbOnly,
            NvidiaAfxProfile.NoiseAndReverb,
            NvidiaAfxProfile.TelephonyUpscale,
            NvidiaAfxProfile.AcousticEchoCancellation
        };

        foreach (NvidiaAfxProfile profile in expected)
        {
            NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(profile);
            Assert.Equal(profile, definition.Profile);
            Assert.False(string.IsNullOrWhiteSpace(definition.Selector));
            Assert.NotEmpty(definition.SupportedSampleRates);
            Assert.NotEmpty(definition.RequiredModels);
            Assert.NotEmpty(definition.PreferredModelRelativePaths);
        }
    }

    [Fact]
    public void NoiseAndReverb_DefaultsToDereverbDenoiserSelector()
    {
        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(NvidiaAfxProfile.NoiseAndReverb);
        Assert.Equal("dereverb_denoiser", definition.Selector);
        Assert.False(definition.RequiresFarEndReference);
    }

    [Fact]
    public void AcousticEchoCancellation_RequiresFarEndReference()
    {
        NvidiaAfxProfileDefinition definition =
            NvidiaAfxProfileCatalog.GetDefinition(NvidiaAfxProfile.AcousticEchoCancellation);
        Assert.Equal("aec", definition.Selector);
        Assert.True(definition.RequiresFarEndReference);
        Assert.False(definition.SupportsIntensityRatio);
    }

    [Fact]
    public void TelephonyUpscale_UsesMaxineChainedSelector()
    {
        NvidiaAfxProfileDefinition definition =
            NvidiaAfxProfileCatalog.GetDefinition(NvidiaAfxProfile.TelephonyUpscale);
        Assert.Equal("superres8kto16k_denoiser16k", definition.Selector);
        Assert.True(definition.IsChainedEffect);
        Assert.Contains(8000, definition.SupportedSampleRates);
        Assert.Equal(16000, definition.OutputSampleRate);
        Assert.Equal(16000, definition.ResolveOutputSampleRate(8000));
    }
}
