using Trackdub.Domain;
using Xunit;

namespace Trackdub.Domain.Tests;

public sealed class HardwarePresetRecommendationEngineTests
{
    [Fact]
    public void Recommend_NoGpu_ReturnsCpuSafe()
    {
        HardwareFingerprint fingerprint = HardwareFingerprint.Create("windows", "x64", null, null, null);
        HardwarePresetRecommendation recommendation = HardwarePresetRecommendationEngine.Recommend(
            fingerprint,
            []);

        Assert.Equal(HardwareQualityPreset.CpuSafe, recommendation.Preset);
        Assert.Equal("fast", recommendation.SuggestedModelTierPreference);
    }

    [Fact]
    public void ToModelTierPreference_CpuSafe_ReturnsFast()
    {
        Assert.Equal("fast", HardwarePresetRecommendation.ToModelTierPreference(HardwareQualityPreset.CpuSafe));
    }

    [Fact]
    public void Recommend_TrtLowLatency_ReturnsTurbo()
    {
        HardwareFingerprint fingerprint = HardwareFingerprint.Create(
            "windows",
            "x64",
            "NVIDIA RTX",
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024);
        StageBenchmarkScenarioResult[] scenarios =
        [
            new(StageBenchmarkScenario.Vad, BenchmarkStatus.Completed, "auto", "trt-rtx", 80, 0.2, null),
            new(StageBenchmarkScenario.Asr, BenchmarkStatus.Completed, "auto", "trt-rtx", 90, 0.3, null),
            new(StageBenchmarkScenario.Translation, BenchmarkStatus.Completed, "auto", "trt-rtx", 85, 0.25, null),
            new(StageBenchmarkScenario.Tts, BenchmarkStatus.Completed, "auto", "trt-rtx", 95, 0.35, null)
        ];

        HardwarePresetRecommendation recommendation = HardwarePresetRecommendationEngine.Recommend(fingerprint, scenarios);

        Assert.Equal(HardwareQualityPreset.Turbo, recommendation.Preset);
        Assert.Equal("fast", recommendation.SuggestedModelTierPreference);
    }

    [Fact]
    public void Recommend_PartialTrtLowLatency_ReturnsBalancedNotTurbo()
    {
        HardwareFingerprint fingerprint = HardwareFingerprint.Create(
            "windows",
            "x64",
            "NVIDIA RTX",
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024);
        StageBenchmarkScenarioResult[] scenarios =
        [
            new(StageBenchmarkScenario.Vad, BenchmarkStatus.Completed, "auto", "trt-rtx", 80, 0.2, null),
            new(StageBenchmarkScenario.Asr, BenchmarkStatus.Failed, "auto", "trt-rtx", null, null, "model missing"),
            new(StageBenchmarkScenario.Translation, BenchmarkStatus.Failed, "auto", "trt-rtx", null, null, "timeout"),
            new(StageBenchmarkScenario.Tts, BenchmarkStatus.Failed, "auto", "trt-rtx", null, null, "timeout")
        ];

        HardwarePresetRecommendation recommendation = HardwarePresetRecommendationEngine.Recommend(fingerprint, scenarios);

        Assert.Equal(HardwareQualityPreset.Balanced, recommendation.Preset);
        Assert.Equal("balanced", recommendation.SuggestedModelTierPreference);
    }

    [Fact]
    public void Recommend_AcceleratedHighLatency_ReturnsBalancedNotQuality()
    {
        HardwareFingerprint fingerprint = HardwareFingerprint.Create(
            "windows",
            "x64",
            "NVIDIA RTX",
            16L * 1024 * 1024 * 1024,
            8L * 1024 * 1024 * 1024);
        StageBenchmarkScenarioResult[] scenarios =
        [
            new(StageBenchmarkScenario.Vad, BenchmarkStatus.Completed, "auto", "dml", 200, 0.5, null),
            new(StageBenchmarkScenario.Asr, BenchmarkStatus.Completed, "auto", "dml", 250, 0.6, null)
        ];

        HardwarePresetRecommendation recommendation = HardwarePresetRecommendationEngine.Recommend(fingerprint, scenarios);

        Assert.Equal(HardwareQualityPreset.Balanced, recommendation.Preset);
        Assert.Equal("balanced", recommendation.SuggestedModelTierPreference);
    }
}
