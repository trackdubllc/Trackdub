namespace Trackdub.Domain;

public static class HardwarePresetRecommendationEngine
{
    private const double TurboWarmLatencyThresholdMs = 120d;

    public static HardwarePresetRecommendation Recommend(
        HardwareFingerprint fingerprint,
        IReadOnlyList<StageBenchmarkScenarioResult> scenarios)
    {
        ArgumentNullException.ThrowIfNull(fingerprint);
        ArgumentNullException.ThrowIfNull(scenarios);

        bool hasGpu = !string.IsNullOrWhiteSpace(fingerprint.GpuDescription);
        StageBenchmarkScenarioResult[] completed = scenarios
            .Where(s => s.Status == BenchmarkStatus.Completed)
            .ToArray();

        if (!hasGpu || completed.Length == 0)
        {
            return Create(
                HardwareQualityPreset.CpuSafe,
                "CPU-safe preset",
                [
                    hasGpu
                        ? "Benchmarks did not complete successfully on the available GPU path."
                        : "No discrete GPU was detected for local acceleration.",
                    "Runtime planning will prefer CPU-safe model tiers and conservative provider fallbacks."
                ]);
        }

        bool allCpu = completed.All(s => IsCpuProvider(s.SelectedProvider));
        if (allCpu)
        {
            return Create(
                HardwareQualityPreset.CpuSafe,
                "CPU-safe preset",
                [
                    "All completed stage benchmarks ran on the CPU execution provider.",
                    "Turbo and quality GPU presets are not recommended until a GPU EP succeeds."
                ]);
        }

        double? averageWarmLatency = completed
            .Select(s => s.WarmLatencyAverageMilliseconds)
            .Where(v => v.HasValue)
            .Select(v => v!.Value)
            .DefaultIfEmpty()
            .Average();

        bool hasAcceleratedProvider = completed.Any(s => IsAcceleratedProvider(s.SelectedProvider));
        bool hasTrt = completed.Any(s => s.SelectedProvider.Contains("trt", StringComparison.OrdinalIgnoreCase));
        bool allPlannedScenariosCompleted = scenarios.Count > 0 &&
            scenarios.All(s => s.Status == BenchmarkStatus.Completed);

        if (allPlannedScenariosCompleted &&
            hasAcceleratedProvider &&
            hasTrt &&
            averageWarmLatency > 0 &&
            averageWarmLatency <= TurboWarmLatencyThresholdMs)
        {
            return Create(
                HardwareQualityPreset.Turbo,
                "Turbo preset",
                [
                    $"Average warm latency {averageWarmLatency:F1} ms across stage workloads.",
                    "TensorRT RTX or catalog GPU execution providers completed all planned stage scenarios.",
                    "Maps to the fast model tier for shorter turnaround."
                ]);
        }

        if (hasAcceleratedProvider && averageWarmLatency > TurboWarmLatencyThresholdMs)
        {
            return Create(
                HardwareQualityPreset.Balanced,
                "Balanced preset",
                [
                    $"Average warm latency {averageWarmLatency:F1} ms is above the turbo band.",
                    "GPU execution providers succeeded; prefer balanced models over heavier quality tiers.",
                    "Maps to the balanced model tier."
                ]);
        }

        return Create(
            HardwareQualityPreset.Balanced,
            "Balanced preset",
            [
                "GPU acceleration is available across stage workloads.",
                "Latency and provider mix fit the default balanced trade-off.",
                "Maps to the balanced model tier."
            ]);
    }

    private static HardwarePresetRecommendation Create(
        HardwareQualityPreset preset,
        string summary,
        IReadOnlyList<string> rationaleLines) =>
        new(
            preset,
            summary,
            rationaleLines,
            ToModelTierPreference(preset));

    private static string ToModelTierPreference(HardwareQualityPreset preset) =>
        HardwarePresetRecommendation.ToModelTierPreference(preset);

    private static bool IsCpuProvider(string provider) =>
        provider.Equals("cpu", StringComparison.OrdinalIgnoreCase);

    private static bool IsAcceleratedProvider(string provider) =>
        !IsCpuProvider(provider);
}
