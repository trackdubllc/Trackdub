// Feature: hardware-matrix-routing, Property 16: Small Model iGPU Preference
// Feature: hardware-matrix-routing, Property 17: Large Model dGPU Preference

using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;
using FsCheck;
using FsCheck.Xunit;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Property-based tests verifying iGPU/dGPU preference behavior in the HardwareMatrix.
/// Small, latency-sensitive models should prefer iGPU; large models should prefer dGPU.
///
/// **Validates: Requirements 7.2, 7.3**
/// </summary>
public sealed class HardwareMatrixGpuPreferencePropertyTests
{
    private static readonly HardwareMatrix Matrix = new();

    /// <summary>
    /// Stages from StageWorkloadProfileCatalog where ModelSizeMb &lt; 50 AND LatencySensitivity == High.
    /// These are: Vad (2 MB, High) and Translation (40 MB, High).
    /// </summary>
    private static readonly RuntimeStage[] SmallLatencySensitiveStages =
        StageWorkloadProfileCatalog.All
            .Where(kvp => kvp.Value.ModelSizeMb < 50 && kvp.Value.LatencySensitivity == LatencySensitivity.High)
            .Select(kvp => kvp.Key)
            .ToArray();

    /// <summary>
    /// Stages from StageWorkloadProfileCatalog where ModelSizeMb >= 50.
    /// These are: Asr (75 MB), Tts (82 MB), Diarization (90 MB), Separation (150 MB).
    /// </summary>
    private static readonly RuntimeStage[] LargeModelStages =
        StageWorkloadProfileCatalog.All
            .Where(kvp => kvp.Value.ModelSizeMb >= 50)
            .Select(kvp => kvp.Key)
            .ToArray();

    // ─────────────────────────────────────────────────────────────────────────
    // Property 16: Small Model iGPU Preference
    // For any stage with ModelSizeMb < 50 and LatencySensitivity == High, and a
    // device set containing both a discrete GPU and an integrated GPU where the
    // iGPU has sufficient VRAM for the model, the iGPU SHALL receive a higher
    // TotalScore than the dGPU.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 16: Small Model iGPU Preference
    ///
    /// For any stage with ModelSizeMb &lt; 50 and LatencySensitivity == High, and a device set
    /// containing both a discrete GPU and an integrated GPU where the iGPU has sufficient VRAM
    /// for the model (PeakMemoryMb does not exceed 80% of iGPU VRAM), the iGPU SHALL receive
    /// a higher TotalScore than the dGPU.
    ///
    /// **Validates: Requirements 7.2**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property SmallModel_iGPU_ScoresHigherThan_dGPU()
    {
        return Prop.ForAll(
            SmallModelScenarioArb(),
            scenario =>
            {
                var ranked = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                // Both devices should be present in the ranking (neither excluded)
                if (ranked.Count < 2)
                    return false.Label("Expected both devices in ranking");

                var iGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.IntegratedGpu);
                var dGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.DiscreteGpu);

                if (iGpuScored is null || dGpuScored is null)
                    return false.Label("Both iGPU and dGPU must be in ranking");

                return (iGpuScored.Score.TotalScore > dGpuScored.Score.TotalScore)
                    .Label($"iGPU ({iGpuScored.Score.TotalScore:F4}) should score higher than dGPU ({dGpuScored.Score.TotalScore:F4}) for stage {scenario.Stage}");
            });
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically for Property 16,
    /// ensuring test discovery works with xunit.runner.visualstudio v3.
    ///
    /// **Validates: Requirements 7.2**
    /// </summary>
    [Fact]
    public void SmallModel_iGPU_Preference_PropertyCheck_ViaFact()
    {
        Prop.ForAll(
            SmallModelScenarioArb(),
            scenario =>
            {
                var ranked = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                if (ranked.Count < 2)
                    return false;

                var iGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.IntegratedGpu);
                var dGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.DiscreteGpu);

                if (iGpuScored is null || dGpuScored is null)
                    return false;

                return iGpuScored.Score.TotalScore > dGpuScored.Score.TotalScore;
            }).QuickCheckThrowOnFailure();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Property 17: Large Model dGPU Preference
    // For any stage with ModelSizeMb >= 50 and a device set containing both a
    // discrete GPU and an integrated GPU, the dGPU SHALL receive a higher
    // TotalScore than the iGPU.
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Property 17: Large Model dGPU Preference
    ///
    /// For any stage with ModelSizeMb >= 50 and a device set containing both a discrete GPU
    /// and an integrated GPU (both with sufficient VRAM), the dGPU SHALL receive a higher
    /// TotalScore than the iGPU.
    ///
    /// **Validates: Requirements 7.3**
    /// </summary>
    [Property(MaxTest = 100)]
    public Property LargeModel_dGPU_ScoresHigherThan_iGPU()
    {
        return Prop.ForAll(
            LargeModelScenarioArb(),
            scenario =>
            {
                var ranked = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                // Both devices should be present in the ranking (neither excluded)
                if (ranked.Count < 2)
                    return false.Label("Expected both devices in ranking");

                var iGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.IntegratedGpu);
                var dGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.DiscreteGpu);

                if (iGpuScored is null || dGpuScored is null)
                    return false.Label("Both iGPU and dGPU must be in ranking");

                return (dGpuScored.Score.TotalScore > iGpuScored.Score.TotalScore)
                    .Label($"dGPU ({dGpuScored.Score.TotalScore:F4}) should score higher than iGPU ({iGpuScored.Score.TotalScore:F4}) for stage {scenario.Stage}");
            });
    }

    /// <summary>
    /// Fallback xUnit [Fact] test that invokes FsCheck programmatically for Property 17,
    /// ensuring test discovery works with xunit.runner.visualstudio v3.
    ///
    /// **Validates: Requirements 7.3**
    /// </summary>
    [Fact]
    public void LargeModel_dGPU_Preference_PropertyCheck_ViaFact()
    {
        Prop.ForAll(
            LargeModelScenarioArb(),
            scenario =>
            {
                var ranked = Matrix.RankDevices(scenario.Stage, scenario.Devices);

                if (ranked.Count < 2)
                    return false;

                var iGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.IntegratedGpu);
                var dGpuScored = ranked.FirstOrDefault(sd => sd.Device.Kind == DeviceKind.DiscreteGpu);

                if (iGpuScored is null || dGpuScored is null)
                    return false;

                return dGpuScored.Score.TotalScore > iGpuScored.Score.TotalScore;
            }).QuickCheckThrowOnFailure();
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Generators
    // ─────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Generates a scenario with a small, latency-sensitive stage and both a dGPU and iGPU
    /// with sufficient VRAM (ratio &lt;= 0.5 for both, ensuring max memory headroom factor).
    /// </summary>
    private static Arbitrary<GpuPreferenceScenario> SmallModelScenarioArb()
    {
        var gen = from stage in Gen.Elements(SmallLatencySensitiveStages)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  // Both devices get generous VRAM so ratio <= 0.5 (MemoryHeadroomFactor = 1.0)
                  // PeakMemoryMb / VRAM <= 0.5 → VRAM >= PeakMemoryMb * 2
                  from iGpuVram in Gen.Choose(profile.PeakMemoryMb * 2, profile.PeakMemoryMb * 10)
                  from dGpuVram in Gen.Choose(profile.PeakMemoryMb * 2, profile.PeakMemoryMb * 20)
                  let dGpu = new DeviceEntry(
                      DeviceKind.DiscreteGpu,
                      0,
                      "NVIDIA GeForce RTX 4080",
                      "NVIDIA",
                      dGpuVram,
                      0,
                      allowedProviders.ToList())
                  let iGpu = new DeviceEntry(
                      DeviceKind.IntegratedGpu,
                      1,
                      "Intel UHD Graphics 770",
                      "Intel",
                      iGpuVram,
                      8000,
                      allowedProviders.ToList())
                  select new GpuPreferenceScenario(stage, new List<DeviceEntry> { dGpu, iGpu });

        return Arb.From(gen);
    }

    /// <summary>
    /// Generates a scenario with a large model stage and both a dGPU and iGPU
    /// with sufficient VRAM (ratio &lt;= 0.5 for both, ensuring max memory headroom factor).
    /// </summary>
    private static Arbitrary<GpuPreferenceScenario> LargeModelScenarioArb()
    {
        var gen = from stage in Gen.Elements(LargeModelStages)
                  let profile = StageWorkloadProfileCatalog.All[stage]
                  let allowedProviders = GetAllowedProvidersForStage(stage)
                  // Both devices get generous VRAM so ratio <= 0.5 (MemoryHeadroomFactor = 1.0)
                  from iGpuVram in Gen.Choose(profile.PeakMemoryMb * 2, profile.PeakMemoryMb * 10)
                  from dGpuVram in Gen.Choose(profile.PeakMemoryMb * 2, profile.PeakMemoryMb * 20)
                  let dGpu = new DeviceEntry(
                      DeviceKind.DiscreteGpu,
                      0,
                      "NVIDIA GeForce RTX 4080",
                      "NVIDIA",
                      dGpuVram,
                      0,
                      allowedProviders.ToList())
                  let iGpu = new DeviceEntry(
                      DeviceKind.IntegratedGpu,
                      1,
                      "Intel UHD Graphics 770",
                      "Intel",
                      iGpuVram,
                      8000,
                      allowedProviders.ToList())
                  select new GpuPreferenceScenario(stage, new List<DeviceEntry> { dGpu, iGpu });

        return Arb.From(gen);
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

    private sealed record GpuPreferenceScenario(
        RuntimeStage Stage,
        IReadOnlyList<DeviceEntry> Devices);
}
