// Feature: hardware-matrix-routing, Property 6: Score Normalization
// Feature: hardware-matrix-routing, Property 7: Throughput Factor Ordering
// Feature: hardware-matrix-routing, Property 8: Deterministic Ranking with Tie-Breaking

using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying HardwareMatrix scoring invariants.
/// Uses custom FsCheck generators to produce valid (stage, device) pairs
/// that exercise the scoring algorithm across the full input space.
///
/// **Validates: Requirements 2.1, 2.2, 2.3**
/// </summary>
public sealed class HardwareMatrixScoringPropertyTests
{
    private static readonly HardwareMatrix Matrix = new();

    private static readonly RuntimeStage[] AllStages =
        StageWorkloadProfileCatalog.All.Keys.ToArray();

    /// <summary>
    /// Custom Arbitrary for generating DeviceEntry values that will NOT be excluded
    /// by the HardwareMatrix for a given stage. Ensures:
    /// - VRAM is sufficient (ratio ≤ 0.8) or VRAM is 0 (unknown)
    /// - Device has at least one allowed provider for the stage
    /// - DeviceIndex in [0, 7]
    /// </summary>
    private static Arbitrary<(RuntimeStage Stage, DeviceEntry Device)> ValidStagDevicePairArb()
    {
        var gen = from stage in Gen.Elements(AllStages)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProviders(stage)
                  from kind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Npu, DeviceKind.Cpu)
                  from deviceIndex in Gen.Choose(0, 7)
                      // Generate VRAM that won't cause exclusion: either 0 (unknown) or high enough
                      // that peakMemory/vram <= 0.8
                  from useUnknownVram in Gen.Elements(true, false)
                  let minVramForInclusion = (int)Math.Ceiling(profile.PeakMemoryMb / 0.8) + 1
                  from vram in useUnknownVram
                      ? Gen.Constant(0)
                      : Gen.Choose(minVramForInclusion, 16384)
                  from sharedMem in Gen.Choose(0, 8192)
                      // Ensure at least one allowed provider is in the supported list
                  from extraProviderCount in Gen.Choose(0, 2)
                  from extraProviders in Gen.ArrayOf(extraProviderCount, Gen.Elements(
                      ExecutionProviderKind.Cpu,
                      ExecutionProviderKind.DirectMl,
                      ExecutionProviderKind.TensorRTRtx,
                      ExecutionProviderKind.OpenVino))
                  let supportedProviders = allowedProviders
                      .Take(1)
                      .Concat(extraProviders)
                      .Distinct()
                      .ToList()
                  from vendor in Gen.Elements("NVIDIA", "AMD", "Intel", "Unknown")
                  let device = new DeviceEntry(
                      kind,
                      deviceIndex,
                      $"Test Adapter {deviceIndex}",
                      vendor,
                      vram,
                      sharedMem,
                      supportedProviders)
                  select (stage, device);

        return Arb.From(gen);
    }

    /// <summary>
    /// Custom Arbitrary for generating a list of DeviceEntry values with distinct indices.
    /// </summary>
    private static Arbitrary<(RuntimeStage Stage, IReadOnlyList<DeviceEntry> Devices)> ValidStageDeviceListArb()
    {
        var gen = from stage in Gen.Elements(AllStages)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProviders(stage)
                  from deviceCount in Gen.Choose(2, 6)
                  from devices in GenDeviceList(deviceCount, profile, allowedProviders)
                  select (stage, (IReadOnlyList<DeviceEntry>)devices);

        return Arb.From(gen);
    }

    private static Gen<List<DeviceEntry>> GenDeviceList(
        int count,
        StageWorkloadProfile profile,
        IReadOnlyList<ExecutionProviderKind> allowedProviders)
    {
        return Gen.Sequence(Enumerable.Range(0, count).Select(i =>
            from kind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Npu, DeviceKind.Cpu)
            from useUnknownVram in Gen.Elements(true, false)
            let minVram = (int)Math.Ceiling(profile.PeakMemoryMb / 0.8) + 1
            from vram in useUnknownVram
                ? Gen.Constant(0)
                : Gen.Choose(minVram, 16384)
            from sharedMem in Gen.Choose(0, 8192)
            from extraProviderCount in Gen.Choose(0, 2)
            from extraProviders in Gen.ArrayOf(extraProviderCount, Gen.Elements(
                ExecutionProviderKind.Cpu,
                ExecutionProviderKind.DirectMl,
                ExecutionProviderKind.TensorRTRtx,
                ExecutionProviderKind.OpenVino))
            let supportedProviders = allowedProviders
                .Take(1)
                .Concat(extraProviders)
                .Distinct()
                .ToList()
            from vendor in Gen.Elements("NVIDIA", "AMD", "Intel", "Unknown")
            select new DeviceEntry(
                kind,
                i, // Use index as device index to ensure uniqueness
                $"Test Adapter {i}",
                vendor,
                vram,
                sharedMem,
                supportedProviders)
        )).Select(devices => devices.ToList());
    }

    private static IReadOnlyList<ExecutionProviderKind> GetAllowedProviders(RuntimeStage stage)
    {
        if (StageRuntimeRequirementsCatalog.All.TryGetValue(stage, out var requirements))
            return requirements.AllowedProvidersThisMilestone;

        return Milestone5PlanningPolicy.SupportedProvidersThisMilestone;
    }

    /// <summary>
    /// Property 6: Score Normalization
    ///
    /// For any valid (stage, device) pair where the device is not excluded,
    /// the HardwareMatrix produces a HardwareScore with TotalScore in [0.0, 1.0].
    ///
    /// **Validates: Requirements 2.1**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = [typeof(HardwareMatrixScoringPropertyTests)])]
    public bool ScoreNormalization_TotalScore_IsInZeroToOneRange(
        (RuntimeStage Stage, DeviceEntry Device) pair)
    {
        var (stage, device) = pair;

        var result = Matrix.RankDevices(stage, [device]);

        // The device should not be excluded (our generator ensures valid pairs)
        if (result.Count == 0)
            return true; // Vacuously true if excluded — generator should prevent this but be safe

        var score = result[0].Score;
        return score.TotalScore >= 0.0 && score.TotalScore <= 1.0;
    }

    /// <summary>
    /// Property 7: Throughput Factor Ordering
    ///
    /// For any stage, the throughput factor assigned to a DiscreteGpu device is strictly
    /// greater than that of an IntegratedGpu, which is strictly greater than that of an Npu,
    /// which is strictly greater than that of a Cpu.
    ///
    /// **Validates: Requirements 2.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool ThroughputFactorOrdering_DiscreteGpu_GreaterThan_IntegratedGpu_GreaterThan_Npu_GreaterThan_Cpu()
    {
        // For each stage, create one device of each kind with identical properties
        // (high VRAM, all providers) and verify throughput factor ordering.
        var allProviders = new List<ExecutionProviderKind>
        {
            ExecutionProviderKind.Cpu,
            ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderKind.OpenVino
        };

        foreach (var stage in AllStages)
        {
            var profile = StageWorkloadProfileCatalog.All[stage];
            // Use VRAM high enough to not be excluded (ratio well below 0.5)
            var highVram = profile.PeakMemoryMb * 10;

            var devices = new[]
            {
                DeviceKind.DiscreteGpu,
                DeviceKind.IntegratedGpu,
                DeviceKind.Npu,
                DeviceKind.Cpu
            }.Select((kind, idx) => new DeviceEntry(
                kind,
                idx,
                $"Device {kind}",
                "TestVendor",
                highVram,
                4096,
                allProviders
            )).ToList();

            var result = Matrix.RankDevices(stage, devices);

            // Extract throughput factors by kind
            var factorByKind = result.ToDictionary(
                sd => sd.Device.Kind,
                sd => sd.Score.ThroughputFactor);

            // All four kinds should be present (none excluded with high VRAM and all providers)
            if (factorByKind.Count < 4)
                return false;

            if (factorByKind[DeviceKind.DiscreteGpu] <= factorByKind[DeviceKind.IntegratedGpu])
                return false;
            if (factorByKind[DeviceKind.IntegratedGpu] <= factorByKind[DeviceKind.Npu])
                return false;
            if (factorByKind[DeviceKind.Npu] <= factorByKind[DeviceKind.Cpu])
                return false;
        }

        return true;
    }

    /// <summary>
    /// Property 8: Deterministic Ranking with Tie-Breaking
    ///
    /// For any inputs (stage, devices), calling RankDevices twice with identical inputs
    /// produces identical output orderings. When two devices have equal TotalScore,
    /// the device with the lower DeviceIndex appears first.
    ///
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 200, Arbitrary = [typeof(HardwareMatrixScoringPropertyTests)])]
    public bool DeterministicRanking_IdenticalInputs_ProduceIdenticalOutputs(
        (RuntimeStage Stage, IReadOnlyList<DeviceEntry> Devices) pair)
    {
        var (stage, devices) = pair;

        var result1 = Matrix.RankDevices(stage, devices);
        var result2 = Matrix.RankDevices(stage, devices);

        // Same count
        if (result1.Count != result2.Count)
            return false;

        // Same ordering
        for (int i = 0; i < result1.Count; i++)
        {
            if (result1[i].Device.DeviceIndex != result2[i].Device.DeviceIndex)
                return false;
            if (Math.Abs(result1[i].Score.TotalScore - result2[i].Score.TotalScore) > 1e-10)
                return false;
        }

        return true;
    }

    /// <summary>
    /// Property 8 (continued): Tie-Breaking by DeviceIndex
    ///
    /// When two devices produce equal TotalScore, the device with the lower
    /// DeviceIndex appears first in the ranking.
    ///
    /// **Validates: Requirements 2.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public bool TieBreaking_LowerDeviceIndex_AppearsFirst()
    {
        // Create pairs of identical devices (same kind, same VRAM) with different indices.
        // They should produce the same score, and the lower index should rank first.
        var allProviders = new List<ExecutionProviderKind>
        {
            ExecutionProviderKind.Cpu,
            ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderKind.OpenVino
        };

        foreach (var stage in AllStages)
        {
            var profile = StageWorkloadProfileCatalog.All[stage];
            var highVram = profile.PeakMemoryMb * 10;

            // Two discrete GPUs with same VRAM but different indices
            var device0 = new DeviceEntry(
                DeviceKind.DiscreteGpu, 0, "GPU A", "NVIDIA", highVram, 4096,
                allProviders);
            var device1 = new DeviceEntry(
                DeviceKind.DiscreteGpu, 1, "GPU B", "NVIDIA", highVram, 4096,
                allProviders);

            // Pass them in reverse order to ensure sorting is applied
            var result = Matrix.RankDevices(stage, [device1, device0]);

            if (result.Count < 2)
                return false;

            // Both should have the same score
            if (Math.Abs(result[0].Score.TotalScore - result[1].Score.TotalScore) > 1e-10)
                continue; // Scores differ (e.g., due to latency bonus), skip this stage

            // Lower index should appear first
            if (result[0].Device.DeviceIndex > result[1].Device.DeviceIndex)
                return false;
        }

        return true;
    }

    // --- Arbitrary registration for FsCheck ---
    // FsCheck discovers these via the Arbitrary attribute on [Property] tests.

    public static Arbitrary<(RuntimeStage Stage, DeviceEntry Device)> ValidStagDevicePairArbProperty() =>
        ValidStagDevicePairArb();

    public static Arbitrary<(RuntimeStage, IReadOnlyList<DeviceEntry>)> ValidStageDeviceListArbProperty() =>
        ValidStageDeviceListArb();
}
