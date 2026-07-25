// Feature: hardware-matrix-routing, Property 9: Affinity Rule Override
// Feature: hardware-matrix-routing, Property 10: Provider Constraint Filtering
// Feature: hardware-matrix-routing, Property 18: Memory Exclusion and Ranking

using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying HardwareMatrix affinity override, provider constraint
/// filtering, and memory exclusion/ranking behavior.
///
/// **Validates: Requirements 2.4, 2.5, 11.3, 12.4, 13.4**
/// </summary>
public sealed class HardwareMatrixAffinityFilteringPropertyTests
{
    private static readonly HardwareMatrix Matrix = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Property 9: Affinity Rule Override
    // For any (stage, devices, affinityRule) where the affinity rule specifies a
    // device that is present in the device list and not excluded, that device SHALL
    // appear at rank 1 in the result regardless of its computed score.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 9: Affinity Rule Override — Pinned device appears at rank 1 when
    /// present and not excluded.
    ///
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property AffinityRule_PinnedDevice_AppearsAtRank1()
    {
        return Prop.ForAll(
            AffinityScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(
                    scenario.Stage,
                    scenario.Devices,
                    scenario.AffinityRule);

                // The pinned device must be at index 0
                return (result.Count > 0
                    && result[0].Device.Kind == scenario.AffinityRule.PreferredKind
                    && (scenario.AffinityRule.PreferredDeviceIndex is null
                        || result[0].Device.DeviceIndex == scenario.AffinityRule.PreferredDeviceIndex))
                    .Label($"Pinned device at rank 1: kind={scenario.AffinityRule.PreferredKind}, " +
                           $"index={scenario.AffinityRule.PreferredDeviceIndex}, " +
                           $"result[0]={result[0].Device.Kind}:{result[0].Device.DeviceIndex}");
            });
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically for Property 9.
    ///
    /// **Validates: Requirements 2.4**
    /// </summary>
    [Fact]
    public void AffinityRule_PinnedDevice_AppearsAtRank1_ViaFact()
    {
        Prop.ForAll(
            AffinityScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(
                    scenario.Stage,
                    scenario.Devices,
                    scenario.AffinityRule);

                return (result.Count > 0
                    && result[0].Device.Kind == scenario.AffinityRule.PreferredKind
                    && (scenario.AffinityRule.PreferredDeviceIndex is null
                        || result[0].Device.DeviceIndex == scenario.AffinityRule.PreferredDeviceIndex))
                    .Label("Pinned device must be at rank 1");
            }).QuickCheckThrowOnFailure();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Property 10: Provider Constraint Filtering
    // For any (stage, devices), every device in the returned ranking SHALL have
    // at least one SupportedProvider that is in the stage's AllowedProviders list.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 10: Provider Constraint Filtering — No device in result has a
    /// disallowed EP.
    ///
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property ProviderConstraint_NoDeviceHasDisallowedEP()
    {
        return Prop.ForAll(
            ProviderFilteringScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                var allowedProviders = GetAllowedProvidersForStage(scenario.Stage);

                return result.All(scored =>
                    scored.Device.SupportedProviders.Any(p => allowedProviders.Contains(p)))
                    .Label($"All {result.Count} devices must have at least one allowed provider. " +
                           $"Allowed: [{string.Join(", ", allowedProviders)}]");
            });
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically for Property 10.
    ///
    /// **Validates: Requirements 2.5**
    /// </summary>
    [Fact]
    public void ProviderConstraint_NoDeviceHasDisallowedEP_ViaFact()
    {
        Prop.ForAll(
            ProviderFilteringScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                var allowedProviders = GetAllowedProvidersForStage(scenario.Stage);

                return result.All(scored =>
                    scored.Device.SupportedProviders.Any(p => allowedProviders.Contains(p)))
                    .Label("All devices must have at least one allowed provider");
            }).QuickCheckThrowOnFailure();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Property 18: Memory Exclusion and Ranking
    // (a) If PeakMemoryMb > DedicatedVramMb and VRAM > 0, device is excluded.
    // (b) If PeakMemoryMb > 80% of VRAM, device receives reduced MemoryHeadroomFactor.
    // (c) If VRAM == 0, device is NOT excluded but ranks below devices with known
    //     sufficient VRAM.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 18a: Devices with known VRAM where PeakMemoryMb exceeds 80% of VRAM
    /// are excluded from the ranking entirely.
    ///
    /// **Validates: Requirements 11.3, 12.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MemoryExclusion_DeviceExceedingVram_IsExcluded()
    {
        return Prop.ForAll(
            MemoryExclusionScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                // The device whose VRAM is insufficient (ratio > 0.8) must not appear
                bool excluded = !result.Any(s =>
                    s.Device.DeviceIndex == scenario.InsufficientDevice.DeviceIndex);

                return excluded.Label(
                    $"Device {scenario.InsufficientDevice.DeviceIndex} with VRAM={scenario.InsufficientDevice.DedicatedVramMb}MB " +
                    $"should be excluded for stage peak={scenario.PeakMemoryMb}MB " +
                    $"(ratio={scenario.PeakMemoryMb / (double)scenario.InsufficientDevice.DedicatedVramMb:F2})");
            });
    }

    /// <summary>
    /// Property 18b: Devices where PeakMemoryMb is between 50% and 80% of VRAM
    /// receive a reduced MemoryHeadroomFactor (less than 1.0).
    ///
    /// **Validates: Requirements 12.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MemoryPenalty_DeviceAbove50Percent_HasReducedHeadroom()
    {
        return Prop.ForAll(
            MemoryPenaltyScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                var penalizedDevice = result.FirstOrDefault(s =>
                    s.Device.DeviceIndex == scenario.PenalizedDevice.DeviceIndex);

                if (penalizedDevice is null)
                    return false.Label("Penalized device should not be excluded (ratio <= 0.8)");

                return (penalizedDevice.Score.MemoryHeadroomFactor < 1.0
                    && penalizedDevice.Score.MemoryHeadroomFactor > 0.0)
                    .Label($"MemoryHeadroomFactor={penalizedDevice.Score.MemoryHeadroomFactor:F3} " +
                           $"should be in (0, 1) for ratio={scenario.PeakMemoryMb / (double)scenario.PenalizedDevice.DedicatedVramMb:F2}");
            });
    }

    /// <summary>
    /// Property 18c: Devices with VRAM=0 are NOT excluded but rank below devices
    /// with known sufficient VRAM.
    ///
    /// **Validates: Requirements 13.4**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property MemoryUnknown_VramZero_NotExcludedButRankedLower()
    {
        return Prop.ForAll(
            VramZeroScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                // The VRAM=0 device must be present in results
                var zeroVramResult = result.FirstOrDefault(s =>
                    s.Device.DeviceIndex == scenario.ZeroVramDevice.DeviceIndex);

                // The sufficient-VRAM device must also be present
                var sufficientResult = result.FirstOrDefault(s =>
                    s.Device.DeviceIndex == scenario.SufficientDevice.DeviceIndex);

                if (zeroVramResult is null)
                    return false.Label("VRAM=0 device must NOT be excluded");

                if (sufficientResult is null)
                    return false.Label("Sufficient VRAM device must be present");

                // The VRAM=0 device must rank below the sufficient device
                var zeroIdx = result.ToList().IndexOf(zeroVramResult);
                var sufficientIdx = result.ToList().IndexOf(sufficientResult);

                return (zeroIdx > sufficientIdx)
                    .Label($"VRAM=0 device at rank {zeroIdx} should be below sufficient device at rank {sufficientIdx}");
            });
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically for Property 18.
    ///
    /// **Validates: Requirements 11.3, 12.4, 13.4**
    /// </summary>
    [Fact]
    public void MemoryExclusionAndRanking_PropertyCheck_ViaFact()
    {
        // 18a: Exclusion
        Prop.ForAll(
            MemoryExclusionScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);
                bool excluded = !result.Any(s =>
                    s.Device.DeviceIndex == scenario.InsufficientDevice.DeviceIndex);
                return excluded.Label("Device exceeding VRAM must be excluded");
            }).QuickCheckThrowOnFailure();

        // 18c: VRAM=0 not excluded
        Prop.ForAll(
            VramZeroScenarioArb(),
            scenario =>
            {
                var result = Matrix.RankDevices(scenario.Stage, scenario.Devices);
                bool present = result.Any(s =>
                    s.Device.DeviceIndex == scenario.ZeroVramDevice.DeviceIndex);
                return present.Label("VRAM=0 device must not be excluded");
            }).QuickCheckThrowOnFailure();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generators and Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private static IReadOnlyList<ExecutionProviderKind> GetAllowedProvidersForStage(RuntimeStage stage)
    {
        if (StageRuntimeRequirementsCatalog.All.TryGetValue(stage, out var requirements))
            return requirements.AllowedProvidersThisMilestone;

        return Milestone5PlanningPolicy.SupportedProvidersThisMilestone;
    }

    /// <summary>
    /// Generates a scenario where an affinity rule points to a device that is present
    /// in the device list and has a valid provider for the stage.
    /// </summary>
    private static Arbitrary<AffinityScenario> AffinityScenarioArb()
    {
        var gen = from stage in Gen.Elements(
                      RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
                      RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation)
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  // Generate the pinned device — must have an allowed provider and sufficient VRAM
                  from pinnedKind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Cpu)
                  from pinnedIndex in Gen.Choose(0, 3)
                  let pinnedVram = profile.PeakMemoryMb * 3 // Ensure well within memory limits
                  let pinnedDevice = new DeviceEntry(
                      pinnedKind,
                      pinnedIndex,
                      $"Pinned Device {pinnedIndex}",
                      "TestVendor",
                      pinnedVram,
                      0,
                      allowedProviders.ToList())
                  // Generate 2-4 other devices that also have allowed providers and sufficient VRAM
                  from otherCount in Gen.Choose(2, 4)
                  from otherDevices in Gen.ListOf(otherCount, GenOtherDevice(allowedProviders, pinnedIndex, profile.PeakMemoryMb))
                  let allDevices = otherDevices.Prepend(pinnedDevice).ToList()
                  let affinityRule = new AffinityRule(stage, pinnedKind, pinnedIndex)
                  select new AffinityScenario(stage, allDevices, affinityRule);

        return Arb.From(gen);
    }

    private static Gen<DeviceEntry> GenOtherDevice(
        IReadOnlyList<ExecutionProviderKind> allowedProviders, int excludeIndex, int peakMemoryMb)
    {
        return from kind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Cpu)
               from index in Gen.Choose(0, 7).Where(i => i != excludeIndex)
               from vramMultiplier in Gen.Choose(3, 10)
               let vram = peakMemoryMb * vramMultiplier / 2 // Ensure sufficient VRAM
               select new DeviceEntry(
                   kind,
                   index,
                   $"Other Device {index}",
                   "TestVendor",
                   vram,
                   0,
                   allowedProviders.ToList());
    }

    /// <summary>
    /// Generates a scenario with devices that have various provider combinations,
    /// including some with only disallowed providers.
    /// </summary>
    private static Arbitrary<ProviderFilteringScenario> ProviderFilteringScenarioArb()
    {
        var allProviders = new[]
        {
            ExecutionProviderKind.Cpu,
            ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderKind.OpenVino
        };

        var gen = from stage in Gen.Elements(
                      RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
                      RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  // Generate devices with random provider subsets (some allowed, some not)
                  from deviceCount in Gen.Choose(2, 6)
                  from devices in Gen.ListOf(deviceCount, GenDeviceWithRandomProviders(allProviders, profile.PeakMemoryMb))
                  select new ProviderFilteringScenario(stage, devices.ToList());

        return Arb.From(gen);
    }

    private static Gen<DeviceEntry> GenDeviceWithRandomProviders(
        ExecutionProviderKind[] allProviders, int peakMemoryMb)
    {
        return from kind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Cpu, DeviceKind.Npu)
               from index in Gen.Choose(0, 7)
               from vram in Gen.Choose(peakMemoryMb * 2, peakMemoryMb * 10) // Sufficient VRAM to avoid memory exclusion
               from providerCount in Gen.Choose(1, allProviders.Length)
               from providers in Gen.Shuffle(allProviders).Select(p => p.Take(providerCount).ToList())
               select new DeviceEntry(
                   kind,
                   index,
                   $"Device {kind} {index}",
                   "TestVendor",
                   vram,
                   0,
                   providers);
    }

    /// <summary>
    /// Generates a scenario where one device has insufficient VRAM (ratio > 0.8).
    /// </summary>
    private static Arbitrary<MemoryExclusionScenario> MemoryExclusionScenarioArb()
    {
        var gen = from stage in Gen.Elements(
                      RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
                      RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  // Generate a device with VRAM such that ratio > 0.8 (PeakMemory/VRAM > 0.8)
                  // VRAM must be > 0 and < PeakMemory / 0.8
                  let maxVramForExclusion = (int)(profile.PeakMemoryMb / 0.81) // Just below the threshold
                  from insufficientVram in Gen.Choose(1, Math.Max(1, maxVramForExclusion))
                      .Where(v => (double)profile.PeakMemoryMb / v > 0.8)
                  from insufficientIndex in Gen.Choose(0, 3)
                  let insufficientDevice = new DeviceEntry(
                      DeviceKind.DiscreteGpu,
                      insufficientIndex,
                      $"Insufficient VRAM Device {insufficientIndex}",
                      "TestVendor",
                      insufficientVram,
                      0,
                      allowedProviders.ToList())
                  // Also include a device with sufficient VRAM
                  from sufficientIndex in Gen.Choose(4, 7)
                  let sufficientDevice = new DeviceEntry(
                      DeviceKind.DiscreteGpu,
                      sufficientIndex,
                      $"Sufficient VRAM Device {sufficientIndex}",
                      "TestVendor",
                      profile.PeakMemoryMb * 5,
                      0,
                      allowedProviders.ToList())
                  select new MemoryExclusionScenario(
                      stage,
                      new List<DeviceEntry> { insufficientDevice, sufficientDevice },
                      insufficientDevice,
                      profile.PeakMemoryMb);

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a scenario where one device has VRAM between 50% and 80% utilization
    /// (should be penalized but not excluded).
    /// </summary>
    private static Arbitrary<MemoryPenaltyScenario> MemoryPenaltyScenarioArb()
    {
        var gen = from stage in Gen.Elements(
                      RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
                      RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  // Generate VRAM such that 0.5 < PeakMemory/VRAM <= 0.8
                  // VRAM must be in range [PeakMemory/0.8, PeakMemory/0.5)
                  let minVram = (int)Math.Ceiling(profile.PeakMemoryMb / 0.79) // ratio just under 0.8
                  let maxVram = (int)(profile.PeakMemoryMb / 0.51) // ratio just over 0.5
                  from penalizedVram in Gen.Choose(minVram, Math.Max(minVram, maxVram))
                      .Where(v => v > 0
                          && (double)profile.PeakMemoryMb / v > 0.5
                          && (double)profile.PeakMemoryMb / v <= 0.8)
                  from penalizedIndex in Gen.Choose(0, 3)
                  let penalizedDevice = new DeviceEntry(
                      DeviceKind.DiscreteGpu,
                      penalizedIndex,
                      $"Penalized Device {penalizedIndex}",
                      "TestVendor",
                      penalizedVram,
                      0,
                      allowedProviders.ToList())
                  select new MemoryPenaltyScenario(
                      stage,
                      new List<DeviceEntry> { penalizedDevice },
                      penalizedDevice,
                      profile.PeakMemoryMb);

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a scenario with a VRAM=0 device and a device with known sufficient VRAM,
    /// both of the same DeviceKind to isolate the memory factor effect.
    /// </summary>
    private static Arbitrary<VramZeroScenario> VramZeroScenarioArb()
    {
        var gen = from stage in Gen.Elements(
                      RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
                      RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  from kind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu)
                      // VRAM=0 device (unknown memory)
                  let zeroVramDevice = new DeviceEntry(
                      kind,
                      5,
                      "Unknown VRAM Device",
                      "TestVendor",
                      0,
                      0,
                      allowedProviders.ToList())
                  // Device with known sufficient VRAM (ratio <= 0.5 for max score)
                  let sufficientDevice = new DeviceEntry(
                      kind,
                      1,
                      "Sufficient VRAM Device",
                      "TestVendor",
                      profile.PeakMemoryMb * 3,
                      0,
                      allowedProviders.ToList())
                  select new VramZeroScenario(
                      stage,
                      new List<DeviceEntry> { zeroVramDevice, sufficientDevice },
                      zeroVramDevice,
                      sufficientDevice);

        return Arb.From(gen);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario Records
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record AffinityScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices,
        AffinityRule AffinityRule);

    private sealed record ProviderFilteringScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices);

    private sealed record MemoryExclusionScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices,
        DeviceEntry InsufficientDevice,
        int PeakMemoryMb);

    private sealed record MemoryPenaltyScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices,
        DeviceEntry PenalizedDevice,
        int PeakMemoryMb);

    private sealed record VramZeroScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices,
        DeviceEntry ZeroVramDevice,
        DeviceEntry SufficientDevice);
}
