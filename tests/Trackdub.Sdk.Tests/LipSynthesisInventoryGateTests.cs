using Trackdub.Application.LipSynthesis;
using Trackdub.Domain;

namespace Trackdub.Sdk.Tests;

public sealed class LipSynthesisInventoryGateTests
{
    [Fact]
    public void IsLicenseApproved_CommercialEntry_ReturnsTrue()
    {
        var entry = new ModelInventoryEntry(
            ModelId: "latentsync",
            DisplayName: "LatentSync",
            Task: "lip-synthesis",
            EngineFamily: "latentsync",
            License: "openrail++",
            CommercialAllowed: true,
            CommercialUseVerified: false,
            State: ModelCacheState.Ready,
            FileSizeBytes: null,
            CachedAtUtc: null,
            FailureReason: null,
            Aliases: ["latentsync-1.6"]);

        Assert.True(LipSynthesisInventoryGate.IsLicenseApproved(entry));
    }

    [Fact]
    public void IsLicenseApproved_NonCommercialEntry_ReturnsFalse()
    {
        var entry = new ModelInventoryEntry(
            ModelId: "latentsync",
            DisplayName: "LatentSync",
            Task: "lip-synthesis",
            EngineFamily: "latentsync",
            License: "openrail++",
            CommercialAllowed: false,
            CommercialUseVerified: false,
            State: ModelCacheState.Ready,
            FileSizeBytes: null,
            CachedAtUtc: null,
            FailureReason: null,
            Aliases: ["latentsync-1.6"]);

        Assert.False(LipSynthesisInventoryGate.IsLicenseApproved(entry));
    }
}
