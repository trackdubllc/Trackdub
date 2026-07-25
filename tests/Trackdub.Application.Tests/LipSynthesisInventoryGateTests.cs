using Trackdub.Application.LipSynthesis;
using Trackdub.Domain;

namespace Trackdub.Application.Tests;

public sealed class LipSynthesisInventoryGateTests
{
    private static ModelInventoryEntry Entry(bool commercialAllowed, bool commercialUseVerified) =>
        new(
            ModelId: "test/lip",
            DisplayName: "Test",
            Task: "lip-synthesis",
            EngineFamily: "test",
            License: "test",
            CommercialAllowed: commercialAllowed,
            CommercialUseVerified: commercialUseVerified,
            State: ModelCacheState.Ready,
            FileSizeBytes: null,
            CachedAtUtc: null,
            FailureReason: null);

    [Fact]
    public void LatentSyncPreSmoke_IsLicenseApproved_AndRequiresExperimentalOptIn()
    {
        ModelInventoryEntry entry = Entry(commercialAllowed: true, commercialUseVerified: false);

        Assert.True(LipSynthesisInventoryGate.IsLicenseApproved(entry));
        Assert.True(LipSynthesisInventoryGate.IsExperimentalEngine(entry));
        Assert.True(LipSynthesisInventoryGate.AllowExperimentalExecution(entry));
    }

    [Fact]
    public void VerifiedCommercialEngine_DoesNotRequireExperimentalOptIn()
    {
        ModelInventoryEntry entry = Entry(commercialAllowed: true, commercialUseVerified: true);

        Assert.True(LipSynthesisInventoryGate.IsLicenseApproved(entry));
        Assert.False(LipSynthesisInventoryGate.IsExperimentalEngine(entry));
        Assert.False(LipSynthesisInventoryGate.AllowExperimentalExecution(entry));
    }

    [Fact]
    public void NonCommercialEngine_IsNotLicenseApproved()
    {
        ModelInventoryEntry entry = Entry(commercialAllowed: false, commercialUseVerified: false);

        Assert.False(LipSynthesisInventoryGate.IsLicenseApproved(entry));
        Assert.True(LipSynthesisInventoryGate.IsExperimentalEngine(entry));
    }
}
