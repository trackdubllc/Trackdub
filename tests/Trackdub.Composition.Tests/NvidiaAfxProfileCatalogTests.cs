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
            Assert.NotEmpty(definition.RequiredModelRelativePaths);
        }
    }

    [Fact]
    public void NoiseAndReverb_DefaultsToDereverbDenoiserSelector()
    {
        NvidiaAfxProfileDefinition definition = NvidiaAfxProfileCatalog.GetDefinition(NvidiaAfxProfile.NoiseAndReverb);
        Assert.Equal("dereverb_denoiser", definition.Selector);
        Assert.False(definition.RequiresFarEndReference);
    }
}
