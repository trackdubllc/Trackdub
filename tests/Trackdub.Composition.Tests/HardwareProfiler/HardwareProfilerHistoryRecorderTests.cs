using Trackdub.Composition.HardwareProfiler;
using Trackdub.Domain;
using Xunit;

namespace Trackdub.Composition.Tests.HardwareProfiler;

public sealed class HardwareProfilerHistoryRecorderTests
{
    [Fact]
    public void MapScenarioToRecord_UsesDeterministicIdAndProfilerModelId()
    {
        Guid evidenceId = Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");
        var fingerprint = HardwareFingerprint.Create("Windows", "x64", "GPU", 16L * 1024 * 1024 * 1024, null);
        var scenario = new StageBenchmarkScenarioResult(
            StageBenchmarkScenario.Asr,
            BenchmarkStatus.Completed,
            "auto",
            "dml",
            42.5,
            0.2,
            null);
        var snapshot = HardwareProfilerSnapshot.Create(
            fingerprint,
            [scenario],
            new HardwarePresetRecommendation(
                HardwareQualityPreset.Balanced,
                "Balanced",
                ["ok"],
                "balanced"));

        string reportsRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            BenchmarkRunRecord record = HardwareProfilerHistoryRecorder.MapScenarioToRecord(
                snapshot with { EvidenceId = evidenceId },
                scenario,
                reportsRoot,
                runCount: 3);

            BenchmarkRunRecord repeat = HardwareProfilerHistoryRecorder.MapScenarioToRecord(
                snapshot with { EvidenceId = evidenceId },
                scenario,
                reportsRoot,
                runCount: 3);

            Assert.Equal(record.Id, repeat.Id);
            Assert.Equal(
                HardwareProfilerHistoryRecorder.BuildProfilerModelId(evidenceId, fingerprint.Hash, "asr"),
                record.ModelId);
            Assert.Equal(BenchmarkStatus.Completed, record.Status);
            Assert.Equal("auto", record.RequestedProvider);
            Assert.Equal("dml", record.SelectedProvider);
            Assert.Equal(42.5, record.WarmLatencyAverageMilliseconds);
        }
        finally
        {
            if (Directory.Exists(reportsRoot))
            {
                Directory.Delete(reportsRoot, recursive: true);
            }
        }
    }
}
