// Feature: hardware-matrix-routing, Property 20: OOM Exclusion Lifecycle

using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying the OOM exclusion lifecycle:
/// 1. When a device is marked as memory-exhausted in a DeviceExclusionSet,
/// 2. The HardwareMatrix excludes that device from scoring results,
/// 3. After ClearRunExclusions is called, the device is no longer excluded.
///
/// **Validates: Requirements 13.1, 13.2**
/// </summary>
public sealed class OomExclusionLifecyclePropertyTests
{
    private static readonly HardwareMatrix Matrix = new();

    // ─────────────────────────────────────────────────────────────────────────
    // Property 20: OOM Exclusion Lifecycle
    // For any device that triggers an OOM exception during session creation in a
    // pipeline run, the DeviceExclusionSet SHALL mark it as memory-exhausted, the
    // HardwareMatrix SHALL exclude it from all subsequent stage rankings in the
    // same run, and the exclusion SHALL be cleared when the pipeline run completes.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 20a: When a device is marked as memory-exhausted, the HardwareMatrix
    /// excludes it from the ranking results for the same run.
    ///
    /// **Validates: Requirements 13.1, 13.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OomMarkedDevice_IsExcludedFromRanking()
    {
        return Prop.ForAll(
            OomExclusionScenarioArb(),
            scenario =>
            {
                var exclusions = new DeviceExclusionSet();
                exclusions.MarkMemoryExhausted(scenario.ExhaustedDevice.DeviceIndex);

                var result = Matrix.RankDevices(
                    scenario.Stage,
                    scenario.Devices,
                    exclusions: exclusions);

                bool exhaustedDeviceExcluded = !result.Any(s =>
                    s.Device.DeviceIndex == scenario.ExhaustedDevice.DeviceIndex);

                return exhaustedDeviceExcluded
                    .Label($"Device {scenario.ExhaustedDevice.DeviceIndex} " +
                           $"({scenario.ExhaustedDevice.AdapterDescription}) " +
                           $"should be excluded after OOM mark");
            });
    }

    /// <summary>
    /// Property 20b: After ClearRunExclusions is called, the previously excluded
    /// device is no longer excluded and appears in the ranking results.
    ///
    /// **Validates: Requirements 13.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property AfterClearRunExclusions_DeviceIsNoLongerExcluded()
    {
        return Prop.ForAll(
            OomExclusionScenarioArb(),
            scenario =>
            {
                var exclusions = new DeviceExclusionSet();
                exclusions.MarkMemoryExhausted(scenario.ExhaustedDevice.DeviceIndex);

                // Verify excluded during the run
                var duringRun = Matrix.RankDevices(
                    scenario.Stage,
                    scenario.Devices,
                    exclusions: exclusions);

                bool excludedDuringRun = !duringRun.Any(s =>
                    s.Device.DeviceIndex == scenario.ExhaustedDevice.DeviceIndex);

                // Clear exclusions (simulating pipeline run completion)
                exclusions.ClearRunExclusions();

                // Verify no longer excluded after clear
                var afterClear = Matrix.RankDevices(
                    scenario.Stage,
                    scenario.Devices,
                    exclusions: exclusions);

                bool presentAfterClear = afterClear.Any(s =>
                    s.Device.DeviceIndex == scenario.ExhaustedDevice.DeviceIndex);

                return (excludedDuringRun && presentAfterClear)
                    .Label($"Device {scenario.ExhaustedDevice.DeviceIndex}: " +
                           $"excluded during run={excludedDuringRun}, " +
                           $"present after clear={presentAfterClear}");
            });
    }

    /// <summary>
    /// Property 20c: The OOM exclusion applies across all stages in the same run —
    /// a device marked exhausted for one stage is also excluded from other stages.
    ///
    /// **Validates: Requirements 13.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property OomExclusion_AppliesToAllStagesInSameRun()
    {
        return Prop.ForAll(
            MultiStageExclusionScenarioArb(),
            scenario =>
            {
                var exclusions = new DeviceExclusionSet();
                exclusions.MarkMemoryExhausted(scenario.ExhaustedDevice.DeviceIndex);

                // Check that the device is excluded from both stages
                var resultStage1 = Matrix.RankDevices(
                    scenario.Stage1,
                    scenario.Devices,
                    exclusions: exclusions);

                var resultStage2 = Matrix.RankDevices(
                    scenario.Stage2,
                    scenario.Devices,
                    exclusions: exclusions);

                bool excludedFromStage1 = !resultStage1.Any(s =>
                    s.Device.DeviceIndex == scenario.ExhaustedDevice.DeviceIndex);

                bool excludedFromStage2 = !resultStage2.Any(s =>
                    s.Device.DeviceIndex == scenario.ExhaustedDevice.DeviceIndex);

                return (excludedFromStage1 && excludedFromStage2)
                    .Label($"Device {scenario.ExhaustedDevice.DeviceIndex} " +
                           $"excluded from stage1={excludedFromStage1}, " +
                           $"excluded from stage2={excludedFromStage2}");
            });
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
    /// Generates a scenario with a device that will be marked as memory-exhausted,
    /// plus other devices that remain eligible. All devices have sufficient VRAM and
    /// valid providers so they would normally appear in the ranking.
    /// </summary>
    private static Arbitrary<OomExclusionScenario> OomExclusionScenarioArb()
    {
        var gen = from stage in Gen.Elements(
                      RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
                      RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation)
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  // Generate the device that will be marked as OOM-exhausted
                  from exhaustedIndex in Gen.Choose(0, 7)
                  from exhaustedKind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu)
                  let exhaustedVram = profile.PeakMemoryMb * 3 // Sufficient VRAM (would not be memory-excluded)
                  let exhaustedDevice = new DeviceEntry(
                      exhaustedKind,
                      exhaustedIndex,
                      $"OOM Device {exhaustedIndex}",
                      "TestVendor",
                      exhaustedVram,
                      0,
                      allowedProviders.ToList())
                  // Generate 1-3 other devices that remain eligible
                  from otherCount in Gen.Choose(1, 3)
                  from otherDevices in Gen.ListOf(otherCount, GenEligibleDevice(allowedProviders, exhaustedIndex, profile.PeakMemoryMb))
                  from insertAt in Gen.Choose(0, otherDevices.Count())
                  let devicesWithExhausted = otherDevices
                      .Take(insertAt)
                      .Concat(new[] { exhaustedDevice })
                      .Concat(otherDevices.Skip(insertAt))
                  let reindexedDevices = ReindexDevices(devicesWithExhausted)
                  let reindexedExhaustedDevice = reindexedDevices.Single(d => d.AdapterDescription == exhaustedDevice.AdapterDescription)
                  select new OomExclusionScenario(stage, reindexedDevices, reindexedExhaustedDevice);

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a scenario with two different stages and a device that will be
    /// marked as exhausted, verifying the exclusion applies across stages.
    /// </summary>
    private static Arbitrary<MultiStageExclusionScenario> MultiStageExclusionScenarioArb()
    {
        var stages = new[]
        {
            RuntimeStage.Vad, RuntimeStage.Asr, RuntimeStage.Translation,
            RuntimeStage.Tts, RuntimeStage.Diarization, RuntimeStage.Separation
        };

        var gen = from stage1 in Gen.Elements(stages)
                  from stage2 in Gen.Elements(stages).Where(s => s != stage1)
                      // Use providers that are allowed for BOTH stages
                  let allowedProviders1 = GetAllowedProvidersForStage(stage1)
                  let allowedProviders2 = GetAllowedProvidersForStage(stage2)
                  let commonProviders = allowedProviders1.Intersect(allowedProviders2).ToList()
                  where commonProviders.Count > 0
                  let profile1 = StageWorkloadProfileCatalog.All[stage1]
                  let profile2 = StageWorkloadProfileCatalog.All[stage2]
                  let maxPeakMemory = Math.Max(profile1.PeakMemoryMb, profile2.PeakMemoryMb)
                  // Generate the device that will be marked as OOM-exhausted
                  from exhaustedIndex in Gen.Choose(0, 7)
                  from exhaustedKind in Gen.Elements(DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu)
                  let exhaustedVram = maxPeakMemory * 3 // Sufficient VRAM for both stages
                  let exhaustedDevice = new DeviceEntry(
                      exhaustedKind,
                      exhaustedIndex,
                      $"OOM Device {exhaustedIndex}",
                      "TestVendor",
                      exhaustedVram,
                      0,
                      commonProviders)
                  // Generate other devices
                  from otherCount in Gen.Choose(1, 2)
                  from otherDevices in Gen.ListOf(otherCount, GenEligibleDeviceForBothStages(commonProviders, exhaustedIndex, maxPeakMemory))
                  from insertAt in Gen.Choose(0, otherDevices.Count())
                  let devicesWithExhausted = otherDevices
                      .Take(insertAt)
                      .Concat(new[] { exhaustedDevice })
                      .Concat(otherDevices.Skip(insertAt))
                  let reindexedDevices = ReindexDevices(devicesWithExhausted)
                  let reindexedExhaustedDevice = reindexedDevices.Single(d => d.AdapterDescription == exhaustedDevice.AdapterDescription)
                  select new MultiStageExclusionScenario(stage1, stage2, reindexedDevices, reindexedExhaustedDevice);

        return Arb.From(gen);
    }

    private static Gen<DeviceEntry> GenEligibleDevice(
        IReadOnlyList<ExecutionProviderKind> allowedProviders, int excludeIndex, int peakMemoryMb)
    {
        DeviceKind[] eligibleKinds = allowedProviders.Any(p => p != ExecutionProviderKind.Cpu)
            ? [DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Cpu]
            : [DeviceKind.Cpu];

        return from kind in Gen.Elements(eligibleKinds)
               from index in Gen.Choose(0, 7).Where(i => i != excludeIndex)
               from vramMultiplier in Gen.Choose(3, 8)
               let vram = peakMemoryMb * vramMultiplier / 2 // Ensure sufficient VRAM
               let supportedProviders = kind == DeviceKind.Cpu
                   ? [ExecutionProviderKind.Cpu]
                   : allowedProviders.Where(p => p != ExecutionProviderKind.Cpu).Distinct().ToList()
               select new DeviceEntry(
                   kind,
                   index,
                   $"Eligible Device {index}",
                   "TestVendor",
                   vram,
                   0,
                    supportedProviders);
    }

    private static Gen<DeviceEntry> GenEligibleDeviceForBothStages(
        IReadOnlyList<ExecutionProviderKind> commonProviders, int excludeIndex, int maxPeakMemoryMb)
    {
        DeviceKind[] eligibleKinds = commonProviders.Any(p => p != ExecutionProviderKind.Cpu)
            ? [DeviceKind.DiscreteGpu, DeviceKind.IntegratedGpu, DeviceKind.Cpu]
            : [DeviceKind.Cpu];

        return from kind in Gen.Elements(eligibleKinds)
               from index in Gen.Choose(0, 7).Where(i => i != excludeIndex)
               from vramMultiplier in Gen.Choose(3, 8)
               let vram = maxPeakMemoryMb * vramMultiplier / 2 // Ensure sufficient VRAM for both stages
               let supportedProviders = kind == DeviceKind.Cpu
                   ? [ExecutionProviderKind.Cpu]
                   : commonProviders.Where(p => p != ExecutionProviderKind.Cpu).Distinct().ToList()
               select new DeviceEntry(
                   kind,
                   index,
                   $"Eligible Device {index}",
                   "TestVendor",
                   vram,
                   0,
                    supportedProviders);
    }

    private static IReadOnlyList<DeviceEntry> ReindexDevices(IEnumerable<DeviceEntry> devices)
    {
        List<DeviceEntry> result = [];
        int nextIndex = 0;
        foreach (DeviceEntry device in devices)
        {
            result.Add(device with { DeviceIndex = nextIndex++ });
        }

        return result;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario Records
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record OomExclusionScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices,
        DeviceEntry ExhaustedDevice);

    private sealed record MultiStageExclusionScenario(
        RuntimeStage Stage1,
        RuntimeStage Stage2,
        IReadOnlyList<DeviceEntry> Devices,
        DeviceEntry ExhaustedDevice);
}
