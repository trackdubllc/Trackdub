// Feature: hardware-matrix-routing, Property 15: Hardware Matrix Determinism

using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying that the HardwareMatrix produces identical rankings
/// for repeated calls with identical inputs.
///
/// The HardwareMatrix is stateless and does not accept a proxy flag parameter. Proxy-mode
/// behavior is exercised at device-enumeration and session-creation layers instead.
/// These tests intentionally validate the narrower contract that ranking depends only on
/// the provided stage and device inputs, with no hidden mutable state.
///
/// **Validates: Requirements 6.6**
/// </summary>
public sealed class HardwareMatrixDeterminismPropertyTests
{
    private static readonly HardwareMatrix Matrix = new();

    private static readonly RuntimeStage[] AllStages =
        StageWorkloadProfileCatalog.All.Keys.ToArray();

    /// <summary>
    /// Property 15: HardwareMatrix determinism for identical inputs.
    ///
    /// For any repeated (stage, devices) input, HardwareMatrix must produce identical rankings.
    /// This is a determinism check, not a proxy-toggle check.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RankDevices_IsDeterministic_ForIdenticalInputs()
    {
        return Prop.ForAll(
            RankingScenarioArb(),
            scenario =>
            {
                var result1 = Matrix.RankDevices(scenario.Stage, scenario.Devices);
                var result2 = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                // Same count
                if (result1.Count != result2.Count)
                    return false.Label(
                        $"Result count mismatch: {result1.Count} vs {result2.Count}");

                // Same ordering and scores
                for (int i = 0; i < result1.Count; i++)
                {
                    if (result1[i].Device.DeviceIndex != result2[i].Device.DeviceIndex)
                        return false.Label(
                            $"Device ordering mismatch at rank {i}: index {result1[i].Device.DeviceIndex} vs {result2[i].Device.DeviceIndex}");

                    if (result1[i].Device.Kind != result2[i].Device.Kind)
                        return false.Label(
                            $"Device kind mismatch at rank {i}: {result1[i].Device.Kind} vs {result2[i].Device.Kind}");

                    double score1 = result1[i].Score.TotalScore;
                    double score2 = result2[i].Score.TotalScore;
                    if (!double.IsFinite(score1) || !double.IsFinite(score2))
                        return false.Label(
                            $"Non-finite score at rank {i}: {score1} vs {score2}");

                    if (Math.Abs(score1 - score2) > 1e-10)
                        return false.Label(
                            $"Score mismatch at rank {i}: {score1} vs {score2}");
                }

                return true.Label("Rankings identical for repeated identical inputs");
            });
    }

    /// <summary>
    /// Property 15 (continued): independent HardwareMatrix instances produce identical rankings.
    ///
    /// Verifies that rankings depend only on the supplied stage and devices, not on any mutable
    /// state within the scoring engine.
    ///
    /// **Validates: Requirements 6.6**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property RankDevices_IsDeterministic_AcrossMatrixInstances()
    {
        return Prop.ForAll(
            RankingScenarioArb(),
            scenario =>
            {
                var matrix1 = new HardwareMatrix();
                var matrix2 = new HardwareMatrix();

                var result1 = matrix1.RankDevices(scenario.Stage, scenario.Devices);
                var result2 = matrix2.RankDevices(scenario.Stage, scenario.Devices);

                if (result1.Count != result2.Count)
                    return false.Label(
                        $"Result count mismatch across instances: {result1.Count} vs {result2.Count}");

                for (int i = 0; i < result1.Count; i++)
                {
                    if (result1[i].Device.DeviceIndex != result2[i].Device.DeviceIndex)
                        return false.Label(
                            $"Device ordering mismatch at rank {i} across instances");

                    double score1 = result1[i].Score.TotalScore;
                    double score2 = result2[i].Score.TotalScore;
                    if (!double.IsFinite(score1) || !double.IsFinite(score2))
                        return false.Label(
                            $"Non-finite score at rank {i} across instances: {score1} vs {score2}");

                    if (Math.Abs(score1 - score2) > 1e-10)
                        return false.Label(
                            $"Score mismatch at rank {i} across instances: {score1} vs {score2}");
                }

                return true.Label("Rankings identical across matrix instances");
            });
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generators
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a stage/device scenario with stage-compatible provider sets and enough VRAM
    /// to avoid memory-based exclusions dominating the result.
    /// </summary>
    private static Arbitrary<RankingScenario> RankingScenarioArb()
    {
        var gen = from stage in Gen.Elements(AllStages)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  let minVram = (int)Math.Ceiling(profile.PeakMemoryMb / 0.8) + 1
                  let stageSupportsOpenVino = allowedProviders.Contains(ExecutionProviderKind.OpenVino)
                  from includeNpu in stageSupportsOpenVino ? Gen.Elements(true, false) : Gen.Constant(false)
                  from npuVram in ChooseVram(minVram, 2000)
                  let npuProviders = stageSupportsOpenVino
                      ? new List<ExecutionProviderKind> { ExecutionProviderKind.OpenVino, ExecutionProviderKind.Cpu }
                      : new List<ExecutionProviderKind> { ExecutionProviderKind.Cpu }
                  let npuDevice = new DeviceEntry(
                      DeviceKind.Npu,
                      0,
                      "Intel AI Boost NPU",
                      "Intel",
                      npuVram,
                      0,
                      (IReadOnlyList<ExecutionProviderKind>)npuProviders)
                  from dGpuVram in ChooseVram(minVram, 16384)
                  let dGpuProviders = allowedProviders.ToList()
                  let dGpu = new DeviceEntry(
                      DeviceKind.DiscreteGpu,
                      0,
                      "NVIDIA GeForce RTX 4080",
                      "NVIDIA",
                      dGpuVram,
                      0,
                      (IReadOnlyList<ExecutionProviderKind>)dGpuProviders)
                  from iGpuVram in ChooseVram(minVram, 4096)
                  let iGpuProviders = new List<ExecutionProviderKind> { ExecutionProviderKind.DirectMl, ExecutionProviderKind.Cpu }
                  let iGpu = new DeviceEntry(
                      DeviceKind.IntegratedGpu,
                      0,
                      "Intel UHD Graphics 770",
                      "Intel",
                      iGpuVram,
                      8192,
                      (IReadOnlyList<ExecutionProviderKind>)iGpuProviders)
                  let cpuProviders = new List<ExecutionProviderKind> { ExecutionProviderKind.Cpu }
                  let cpu = new DeviceEntry(
                      DeviceKind.Cpu,
                      0,
                      "Intel Core i9-14900K",
                      "Intel",
                      0,
                      32768,
                      (IReadOnlyList<ExecutionProviderKind>)cpuProviders)
                  from includeGpu in Gen.Elements(true, false)
                  from includeIGpu in Gen.Elements(true, false)
                  let devices = BuildDeviceList(npuDevice, dGpu, iGpu, cpu, includeNpu, includeGpu, includeIGpu)
                  select new RankingScenario(stage, devices);

        return Arb.From(gen);
    }

    private static Gen<int> ChooseVram(int minVram, int nominalUpperBound)
    {
        return Gen.Choose(minVram, Math.Max(minVram, nominalUpperBound));
    }

    private static IReadOnlyList<DeviceEntry> BuildDeviceList(
        DeviceEntry npu,
        DeviceEntry dGpu,
        DeviceEntry iGpu,
        DeviceEntry cpu,
        bool includeNpu,
        bool includeGpu,
        bool includeIGpu)
    {
        var list = new List<DeviceEntry>();
        int nextDeviceIndex = 0;

        if (includeGpu)
            list.Add(dGpu with { DeviceIndex = nextDeviceIndex++ });
        if (includeIGpu)
            list.Add(iGpu with { DeviceIndex = nextDeviceIndex++ });

        if (includeNpu)
            list.Add(npu with { DeviceIndex = nextDeviceIndex++ });

        list.Add(cpu with { DeviceIndex = nextDeviceIndex });

        return list;
    }

    private static IReadOnlyList<ExecutionProviderKind> GetAllowedProvidersForStage(RuntimeStage stage)
    {
        if (StageRuntimeRequirementsCatalog.All.TryGetValue(stage, out var requirements))
            return requirements.AllowedProvidersThisMilestone;

        return Milestone5PlanningPolicy.SupportedProvidersThisMilestone;
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Scenario Record
    // ─────────────────────────────────────────────────────────────────────────

    private sealed record RankingScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices);
}
