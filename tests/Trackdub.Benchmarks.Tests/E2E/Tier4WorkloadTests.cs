using System.Text.Json;
using Trackdub.Benchmarks.Tests.E2E.Contracts;
using Trackdub.Benchmarks.Tests.E2E.Fixtures;
using Trackdub.Benchmarks.Tests.E2E.Oracles;
using Trackdub.Contracts.Benchmarking;

namespace Trackdub.Benchmarks.Tests.E2E;

/// <summary>
/// Tier 4: Real-world application scenarios simulating speech dubbing workloads (>=5 workloads).
/// </summary>
public sealed class Tier4WorkloadTests : IDisposable
{
    private readonly MockDubbingBenchmarkHarness _harness = new();

    // =========================================================================
    // Workload 1: Short Speech Clip (5-second greeting / one-liner)
    // =========================================================================
    [Fact]
    public void Workload1_ShortSpeechClip_DubbingBenchmark()
    {
        string fixturePath = _harness.CreateTempAudioFixture(durationSeconds: 5.0);

        var stageDurations = new Dictionary<string, double>
        {
            ["audio-prep"] = 4.2,
            ["separation"] = 18.5,
            ["transcription"] = 32.0,
            ["alignment"] = 12.0,
            ["dubbing"] = 24.5,
        };

        BenchmarkEvidenceReport report = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "short-clip-greeting",
            provider: "cpu",
            stageDurations: stageDurations,
            workingSetStartBytes: Mb(80),
            workingSetEndBytes: Mb(110),
            peakWorkingSetBytes: Mb(125),
            managedAllocatedBytes: Mb(12));

        Assert.Equal(BenchmarkEvidenceStatus.Completed, report.Status);
        Assert.Equal(5, report.Stages.Count);

        double totalDurationMs = report.TimingsMilliseconds["pipeline"]!.Value;
        Assert.Equal(91.2, totalDurationMs);

        // Compute throughput: 5 audio seconds in ~91ms processing time
        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(
            [totalDurationMs],
            totalUnits: 5.0,
            totalDurationSeconds: totalDurationMs / 1000.0);

        Assert.True(stats.ThroughputUnitsPerSecond > 50.0, "Throughput should be > 50x real-time.");
    }

    // =========================================================================
    // Workload 2: Long-Form Podcast Segment (60-second monologue)
    // =========================================================================
    [Fact]
    public void Workload2_LongFormPodcast_DubbingBenchmark()
    {
        string fixturePath = _harness.CreateTempAudioFixture(durationSeconds: 60.0);

        var stageDurations = new Dictionary<string, double>
        {
            ["audio-prep"] = 35.0,
            ["separation"] = 145.0,
            ["transcription"] = 310.0,
            ["alignment"] = 110.0,
            ["dubbing"] = 280.0,
        };

        BenchmarkEvidenceReport report = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "podcast-monologue-60s",
            provider: "directml",
            stageDurations: stageDurations,
            workingSetStartBytes: Mb(150),
            workingSetEndBytes: Mb(280),
            peakWorkingSetBytes: Mb(320),
            managedAllocatedBytes: Mb(85),
            gen0: 12,
            gen1: 4,
            gen2: 1);

        double totalMs = report.TimingsMilliseconds["pipeline"]!.Value;
        Assert.Equal(880.0, totalMs);

        // Throughput: 60 audio seconds in 0.880 wall-clock seconds = ~68x RTF
        LatencyStatistics stats = BenchmarkCalculationOracle.CalculatePercentiles(
            [totalMs],
            totalUnits: 60.0,
            totalDurationSeconds: totalMs / 1000.0);

        Assert.True(stats.ThroughputUnitsPerSecond > 65.0, "Long form podcast throughput should exceed 65x.");
    }

    // =========================================================================
    // Workload 3: Multi-Speaker Dialogue (Interview / 2 speakers)
    // =========================================================================
    [Fact]
    public void Workload3_MultiSpeakerDialogue_DubbingBenchmark()
    {
        string fixturePath = _harness.CreateTempAudioFixture(durationSeconds: 30.0);

        var stageDurations = new Dictionary<string, double>
        {
            ["audio-prep"] = 18.0,
            ["separation"] = 65.0,
            ["diarization"] = 45.0,
            ["transcription"] = 120.0,
            ["alignment"] = 55.0,
            ["tts-speaker-1"] = 70.0,
            ["tts-speaker-2"] = 75.0,
        };

        BenchmarkEvidenceReport report = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "dialogue-interview",
            provider: "cpu",
            stageDurations: stageDurations,
            workingSetStartBytes: Mb(120),
            workingSetEndBytes: Mb(210),
            peakWorkingSetBytes: Mb(240),
            managedAllocatedBytes: Mb(45));

        Assert.Equal(7, report.Stages.Count);
        Assert.NotNull(report.Stages.FirstOrDefault(s => s.Name == "diarization"));
        Assert.NotNull(report.Stages.FirstOrDefault(s => s.Name == "tts-speaker-1"));
        Assert.NotNull(report.Stages.FirstOrDefault(s => s.Name == "tts-speaker-2"));

        double totalMs = report.TimingsMilliseconds["pipeline"]!.Value;
        Assert.Equal(448.0, totalMs);
    }

    // =========================================================================
    // Workload 4: Degraded Noisy Audio (Enhancement + VAD + Separation)
    // =========================================================================
    [Fact]
    public void Workload4_DegradedNoisyAudio_DubbingBenchmark()
    {
        string fixturePath = _harness.CreateTempAudioFixture(durationSeconds: 15.0);

        var stageDurations = new Dictionary<string, double>
        {
            ["audio-prep"] = 10.0,
            ["speech-enhancement"] = 85.0,
            ["vad"] = 15.0,
            ["separation"] = 45.0,
            ["transcription"] = 70.0,
            ["alignment"] = 28.0,
            ["dubbing"] = 52.0,
        };

        BenchmarkEvidenceReport report = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "noisy-outdoor-audio",
            provider: "directml",
            stageDurations: stageDurations,
            workingSetStartBytes: Mb(140),
            workingSetEndBytes: Mb(260),
            peakWorkingSetBytes: Mb(310),
            managedAllocatedBytes: Mb(55));

        Assert.Equal(7, report.Stages.Count);
        Assert.True(report.Stages.First(s => s.Name == "speech-enhancement").DurationMilliseconds > 50.0);
    }

    // =========================================================================
    // Workload 5: Multi-Target Language Matrix (English -> Spanish & Japanese)
    // =========================================================================
    [Fact]
    public void Workload5_MultiTargetLanguageMatrix_DubbingBenchmark()
    {
        string fixturePath = _harness.CreateTempAudioFixture(durationSeconds: 20.0);

        // Target language 1: Spanish
        var esStages = new Dictionary<string, double>
        {
            ["audio-prep"] = 12.0,
            ["separation"] = 48.0,
            ["transcription"] = 80.0,
            ["translation-es"] = 25.0,
            ["alignment-es"] = 30.0,
            ["dubbing-es"] = 60.0,
        };

        // Target language 2: Japanese
        var jaStages = new Dictionary<string, double>
        {
            ["audio-prep"] = 12.0,
            ["separation"] = 48.0,
            ["transcription"] = 80.0,
            ["translation-ja"] = 35.0,
            ["alignment-ja"] = 40.0,
            ["dubbing-ja"] = 75.0,
        };

        BenchmarkEvidenceReport esReport = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "multi-lang-es",
            provider: "cpu",
            stageDurations: esStages);

        BenchmarkEvidenceReport jaReport = MockDubbingBenchmarkHarness.CreateMockEvidenceReport(
            scenario: "multi-lang-ja",
            provider: "cpu",
            stageDurations: jaStages);

        double esTotal = esReport.TimingsMilliseconds["pipeline"]!.Value;
        double jaTotal = jaReport.TimingsMilliseconds["pipeline"]!.Value;

        Assert.Equal(255.0, esTotal);
        Assert.Equal(290.0, jaTotal);

        // Japanese phoneme/TTS pipeline has higher latency than Spanish
        Assert.True(jaTotal > esTotal);
    }

    public void Dispose() => _harness.Dispose();

    private static long Mb(long megabytes) => megabytes * 1024L * 1024L;
}
