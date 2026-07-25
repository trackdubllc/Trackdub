namespace Trackdub.Domain;

public enum HardwareQualityPreset
{
    Quality = 0,
    Balanced = 1,
    Turbo = 2,
    CpuSafe = 3
}

public enum StageBenchmarkScenario
{
    Vad = 1,
    Asr = 2,
    Translation = 3,
    Tts = 4
}

public sealed record HardwareFingerprint(
    string Hash,
    string OperatingSystem,
    string Architecture,
    string? GpuDescription,
    long? TotalRamBytes,
    long? GpuDedicatedMemoryBytes,
    DateTimeOffset CapturedAtUtc)
{
    public static HardwareFingerprint Create(
        string operatingSystem,
        string architecture,
        string? gpuDescription,
        long? totalRamBytes,
        long? gpuDedicatedMemoryBytes)
    {
        string normalizedGpu = string.IsNullOrWhiteSpace(gpuDescription) ? "none" : gpuDescription.Trim();
        string payload = string.Join(
            '|',
            operatingSystem.Trim().ToLowerInvariant(),
            architecture.Trim().ToLowerInvariant(),
            normalizedGpu.ToLowerInvariant(),
            totalRamBytes?.ToString() ?? "0",
            gpuDedicatedMemoryBytes?.ToString() ?? "0");

        string hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(payload))).ToLowerInvariant();

        return new HardwareFingerprint(
            hash,
            operatingSystem,
            architecture,
            string.IsNullOrWhiteSpace(gpuDescription) ? null : gpuDescription.Trim(),
            totalRamBytes,
            gpuDedicatedMemoryBytes,
            DateTimeOffset.UtcNow);
    }
}

public sealed record StageBenchmarkScenarioResult(
    StageBenchmarkScenario Scenario,
    BenchmarkStatus Status,
    string RequestedProvider,
    string SelectedProvider,
    double? WarmLatencyAverageMilliseconds,
    double? RealTimeFactorAverage,
    string? FailureReason)
{
    public RuntimeStage RuntimeStage => Scenario switch
    {
        StageBenchmarkScenario.Vad => RuntimeStage.Vad,
        StageBenchmarkScenario.Asr => RuntimeStage.Asr,
        StageBenchmarkScenario.Translation => RuntimeStage.Translation,
        StageBenchmarkScenario.Tts => RuntimeStage.Tts,
        _ => throw new ArgumentOutOfRangeException(nameof(Scenario), Scenario, "Unknown benchmark scenario.")
    };
}

public sealed record HardwareProfilerSnapshot(
    Guid EvidenceId,
    HardwareFingerprint Fingerprint,
    IReadOnlyList<StageBenchmarkScenarioResult> Scenarios,
    HardwarePresetRecommendation Recommendation,
    DateTimeOffset CompletedAtUtc)
{
    public static HardwareProfilerSnapshot Create(
        HardwareFingerprint fingerprint,
        IReadOnlyList<StageBenchmarkScenarioResult> scenarios,
        HardwarePresetRecommendation recommendation) =>
        new(
            Guid.NewGuid(),
            fingerprint,
            scenarios,
            recommendation,
            DateTimeOffset.UtcNow);
}

public sealed record HardwarePresetRecommendation(
    HardwareQualityPreset Preset,
    string Summary,
    IReadOnlyList<string> RationaleLines,
    string SuggestedModelTierPreference)
{
    public static string ToModelTierPreference(HardwareQualityPreset preset) =>
        preset switch
        {
            HardwareQualityPreset.Quality => "quality",
            HardwareQualityPreset.Balanced => "balanced",
            HardwareQualityPreset.Turbo => "fast",
            HardwareQualityPreset.CpuSafe => "fast",
            _ => "balanced"
        };

    public static string ToSettingsKey(HardwareQualityPreset preset) =>
        preset switch
        {
            HardwareQualityPreset.Quality => "quality",
            HardwareQualityPreset.Balanced => "balanced",
            HardwareQualityPreset.Turbo => "turbo",
            HardwareQualityPreset.CpuSafe => "cpu-safe",
            _ => "balanced"
        };

    public static bool TryParseSettingsKey(string? key, out HardwareQualityPreset preset)
    {
        if (string.IsNullOrWhiteSpace(key))
        {
            preset = HardwareQualityPreset.Balanced;
            return false;
        }

        switch (key.Trim().ToLowerInvariant())
        {
            case "quality":
                preset = HardwareQualityPreset.Quality;
                return true;
            case "balanced":
                preset = HardwareQualityPreset.Balanced;
                return true;
            case "turbo":
            case "fast":
                preset = HardwareQualityPreset.Turbo;
                return true;
            case "cpu-safe":
            case "cpusafe":
            case "cpu":
                preset = HardwareQualityPreset.CpuSafe;
                return true;
            default:
                preset = HardwareQualityPreset.Balanced;
                return false;
        }
    }
}
