using Trackdub.Domain;

namespace Trackdub.Inference.Runtime.Planning;

/// <summary>
/// Stateless scoring engine that ranks available compute devices for a given pipeline stage
/// based on throughput, memory headroom, and latency sensitivity factors.
/// </summary>
public sealed class HardwareMatrix : IHardwareMatrix
{
    private const double ThroughputWeight = 0.4;
    private const double MemoryWeight = 0.4;
    private const double LatencyBonusValue = 0.2;
    private const double MemoryRatioSafeThreshold = 0.5;
    private const double MemoryRatioExcludeThreshold = 0.8;
    private const double UnknownVramFactor = 0.3;

    /// <inheritdoc />
    public IReadOnlyList<ScoredDevice> RankDevices(
        RuntimeStage stage,
        IReadOnlyList<DeviceEntry> devices,
        AffinityRule? affinityRule = null,
        DeviceExclusionSet? exclusions = null,
        int? peakVramMb = null,
        int? minVramMb = null)
    {
        var profile = StageWorkloadProfileCatalog.All[stage];
        var effectivePeak = peakVramMb ?? profile.PeakMemoryMb;
        var allowedProviders = GetAllowedProviders(stage);

        var candidates = new List<ScoredDevice>();

        foreach (var device in devices)
        {
            if (exclusions is not null && exclusions.IsExcluded(device.DeviceIndex))
                continue;

            if (!HasAllowedProvider(device, allowedProviders))
                continue;

            if (device.DedicatedVramMb > 0)
            {
                if (minVramMb.HasValue)
                {
                    // Hard-exclude below model minimum; above it partial offload is allowed.
                    if (device.DedicatedVramMb < minVramMb.Value)
                        continue;
                }
                else
                {
                    var ratio = (double)effectivePeak / device.DedicatedVramMb;
                    if (ratio > MemoryRatioExcludeThreshold)
                        continue;
                }
            }

            bool isPartialOffload = peakVramMb.HasValue &&
                                    device.DedicatedVramMb > 0 &&
                                    device.DedicatedVramMb < peakVramMb.Value &&
                                    (!minVramMb.HasValue || device.DedicatedVramMb >= minVramMb.Value);

            var score = ComputeScore(device, profile, effectivePeak);
            candidates.Add(new ScoredDevice(device, score, isPartialOffload));
        }

        // Sort by score descending, tie-break by DeviceIndex ascending.
        candidates.Sort(static (a, b) =>
        {
            var scoreComparison = b.Score.TotalScore.CompareTo(a.Score.TotalScore);
            return scoreComparison != 0
                ? scoreComparison
                : a.Device.DeviceIndex.CompareTo(b.Device.DeviceIndex);
        });

        // Apply affinity rule override: move pinned device to rank 1 if present.
        if (affinityRule is not null)
        {
            var pinnedIndex = FindPinnedDeviceIndex(candidates, affinityRule);
            if (pinnedIndex > 0)
            {
                var pinned = candidates[pinnedIndex];
                candidates.RemoveAt(pinnedIndex);
                candidates.Insert(0, pinned);
            }
        }

        return candidates;
    }

    private static HardwareScore ComputeScore(DeviceEntry device, StageWorkloadProfile profile, int effectivePeakVram)
    {
        var throughputFactor = GetThroughputFactor(device.Kind);
        var memoryHeadroomFactor = ComputeMemoryHeadroomFactor(device, effectivePeakVram);
        var latencyBonus = ComputeLatencyBonus(device, profile);

        var totalScore = (ThroughputWeight * throughputFactor)
                       + (MemoryWeight * memoryHeadroomFactor)
                       + latencyBonus;

        return new HardwareScore(totalScore, throughputFactor, memoryHeadroomFactor, latencyBonus);
    }

    private static double GetThroughputFactor(DeviceKind kind) => kind switch
    {
        DeviceKind.DiscreteGpu => 1.0,
        DeviceKind.IntegratedGpu => 0.6,
        DeviceKind.Npu => 0.3,
        DeviceKind.Cpu => 0.2,
        _ => 0.0
    };

    private static double ComputeMemoryHeadroomFactor(DeviceEntry device, int peakMemoryMb)
    {
        if (device.DedicatedVramMb == 0)
            return UnknownVramFactor;

        var ratio = (double)peakMemoryMb / device.DedicatedVramMb;

        if (ratio <= MemoryRatioSafeThreshold)
            return 1.0;

        // Linear decrease from 1.0 to 0.0 as ratio goes from 0.5 to 0.8.
        return 1.0 - ((ratio - MemoryRatioSafeThreshold) / (MemoryRatioExcludeThreshold - MemoryRatioSafeThreshold));
    }

    private static double ComputeLatencyBonus(DeviceEntry device, StageWorkloadProfile profile)
    {
        if (profile.LatencySensitivity != LatencySensitivity.High || profile.ModelSizeMb > 50)
            return 0.0;

        return device.Kind is DeviceKind.IntegratedGpu or DeviceKind.Npu
            ? LatencyBonusValue
            : 0.0;
    }

    private static bool HasAllowedProvider(DeviceEntry device, IReadOnlyList<ExecutionProviderKind> allowedProviders)
    {
        foreach (var provider in device.SupportedProviders)
        {
            foreach (var allowed in allowedProviders)
            {
                if (provider == allowed)
                    return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<ExecutionProviderKind> GetAllowedProviders(RuntimeStage stage)
    {
        if (StageRuntimeRequirementsCatalog.All.TryGetValue(stage, out var requirements))
            return requirements.AllowedProvidersThisMilestone;

        // Fallback: allow all milestone-supported providers.
        return Milestone5PlanningPolicy.SupportedProvidersThisMilestone;
    }

    private static int FindPinnedDeviceIndex(List<ScoredDevice> candidates, AffinityRule affinityRule)
    {
        for (var i = 0; i < candidates.Count; i++)
        {
            var device = candidates[i].Device;
            if (device.Kind != affinityRule.PreferredKind)
                continue;

            if (affinityRule.PreferredDeviceIndex is null || device.DeviceIndex == affinityRule.PreferredDeviceIndex)
                return i;
        }

        return -1;
    }
}
