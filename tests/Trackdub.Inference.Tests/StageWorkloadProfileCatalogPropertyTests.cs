// Feature: hardware-matrix-routing, Property 19: Workload Profile Catalog Completeness
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying that the StageWorkloadProfileCatalog is complete and valid.
/// Every RuntimeStage enum member must have exactly one entry with valid ranges.
///
/// **Validates: Requirements 12.1, 12.3**
/// </summary>
public sealed class StageWorkloadProfileCatalogPropertyTests
{
    /// <summary>
    /// Property 19: Workload Profile Catalog Completeness
    ///
    /// For every member of the RuntimeStage enum, the StageWorkloadProfileCatalog SHALL contain
    /// exactly one StageWorkloadProfile entry with ModelSizeMb in [1, 1000], PeakMemoryMb in
    /// [1, 3000], and a valid LatencySensitivity value.
    ///
    /// **Validates: Requirements 12.1, 12.3**
    /// </summary>
    [Fact]
    public void EveryStageMember_HasExactlyOneValidEntry()
    {
        var allStages = Enum.GetValues<RuntimeStage>();
        var catalog = StageWorkloadProfileCatalog.All;

        // Catalog has exactly as many entries as there are RuntimeStage enum members (no extras)
        Assert.Equal(allStages.Length, catalog.Count);

        foreach (var stage in allStages)
        {
            // Each stage has an entry
            Assert.True(catalog.ContainsKey(stage),
                $"StageWorkloadProfileCatalog is missing an entry for RuntimeStage.{stage}");

            var profile = catalog[stage];

            // Entry's Stage field matches the key
            Assert.Equal(stage, profile.Stage);

            // ModelSizeMb in [1, 1000]
            Assert.InRange(profile.ModelSizeMb, 1, 1000);

            // PeakMemoryMb in [1, 3000]
            Assert.InRange(profile.PeakMemoryMb, 1, 3000);

            // LatencySensitivity is a valid enum value
            Assert.True(Enum.IsDefined(profile.LatencySensitivity),
                $"RuntimeStage.{stage} has invalid LatencySensitivity value: {profile.LatencySensitivity}");
        }
    }

    /// <summary>
    /// Property 19 (property-based variant): For any RuntimeStage drawn from the enum,
    /// the catalog contains a valid entry.
    ///
    /// This uses FsCheck to generate random RuntimeStage values, ensuring the property holds
    /// for all enum members regardless of enumeration order.
    ///
    /// **Validates: Requirements 12.1, 12.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property AnyRuntimeStage_HasValidCatalogEntry()
    {
        var stageArb = Gen.Elements(Enum.GetValues<RuntimeStage>()).ToArbitrary();

        return Prop.ForAll(stageArb, stage =>
        {
            var catalog = StageWorkloadProfileCatalog.All;

            // Entry exists
            if (!catalog.TryGetValue(stage, out var profile))
                return false;

            // Stage field matches
            if (profile.Stage != stage)
                return false;

            // ModelSizeMb in [1, 1000]
            if (profile.ModelSizeMb < 1 || profile.ModelSizeMb > 1000)
                return false;

            // PeakMemoryMb in [1, 3000]
            if (profile.PeakMemoryMb < 1 || profile.PeakMemoryMb > 3000)
                return false;

            // LatencySensitivity is a valid enum value
            if (!Enum.IsDefined(profile.LatencySensitivity))
                return false;

            return true;
        });
    }

    /// <summary>
    /// Verifies the catalog has no extra entries beyond the defined RuntimeStage enum members.
    ///
    /// **Validates: Requirements 12.1, 12.3**
    /// </summary>
    [Fact]
    public void Catalog_HasNoExtraEntries()
    {
        var allStages = new HashSet<RuntimeStage>(Enum.GetValues<RuntimeStage>());
        var catalogKeys = StageWorkloadProfileCatalog.All.Keys;

        foreach (var key in catalogKeys)
        {
            Assert.Contains(key, allStages);
        }
    }
}
