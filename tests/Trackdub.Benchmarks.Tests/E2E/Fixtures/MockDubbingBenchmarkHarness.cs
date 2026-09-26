using Trackdub.Contracts.Benchmarking;
using Trackdub.TestDoubles;

namespace Trackdub.Benchmarks.Tests.E2E.Fixtures;

/// <summary>
/// Deterministic mock harness providing synthetic workloads, test doubles,
/// and mock benchmark evidence without network, external FFmpeg, or live models.
/// </summary>
public sealed class MockDubbingBenchmarkHarness : IDisposable
{
    private readonly List<string> _tempFiles = [];

    /// <summary>
    /// Creates a temporary WAV audio file with valid PCM-16 RIFF headers.
    /// </summary>
    public string CreateTempAudioFixture(double durationSeconds = 1.0, int sampleRate = 16000)
    {
        string path = Path.Combine(Path.GetTempPath(), $"trackdub_bench_{Guid.NewGuid():N}.wav");
        byte[] wavBytes = FakeWavHelper.MinimalPcm16(durationSeconds, sampleRate);
        File.WriteAllBytes(path, wavBytes);
        _tempFiles.Add(path);
        return path;
    }

    /// <summary>
    /// Builds a synthetic BenchmarkEvidenceReport simulating a complete pipeline run.
    /// </summary>
    public static BenchmarkEvidenceReport CreateMockEvidenceReport(
        string scenario,
        string provider,
        IReadOnlyDictionary<string, double> stageDurations,
        long workingSetStartBytes = 100 * 1024 * 1024,
        long workingSetEndBytes = 150 * 1024 * 1024,
        long peakWorkingSetBytes = 160 * 1024 * 1024,
        long managedAllocatedBytes = 25 * 1024 * 1024,
        int gen0 = 5,
        int gen1 = 2,
        int gen2 = 1,
        BenchmarkEvidenceStatus status = BenchmarkEvidenceStatus.Completed,
        string fixtureSha256 = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")
    {
        double totalDuration = stageDurations.Values.Sum();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        var stages = stageDurations.Select(kvp => new BenchmarkEvidenceStage
        {
            Name = kvp.Key,
            Status = status,
            DurationMilliseconds = kvp.Value,
            RequestedProvider = provider,
            ActualProvider = provider,
            RequestedModel = "mock-model",
            ActualModel = "mock-model",
            StartedAtUtc = now.AddMilliseconds(-totalDuration),
            CompletedAtUtc = now,
        }).ToList();

        var timings = new Dictionary<string, double?>
        {
            ["pipeline"] = totalDuration,
        };
        foreach (var kvp in stageDurations)
        {
            timings[$"stage:{kvp.Key}:duration"] = kvp.Value;
        }

        var memory = new Dictionary<string, long?>
        {
            ["processWorkingSetStart"] = workingSetStartBytes,
            ["processWorkingSetEnd"] = workingSetEndBytes,
            ["peakWorkingSetBytes"] = peakWorkingSetBytes,
            ["managedAllocatedBytes"] = managedAllocatedBytes,
            ["gen0Collections"] = gen0,
            ["gen1Collections"] = gen1,
            ["gen2Collections"] = gen2,
        };

        return new BenchmarkEvidenceReport
        {
            RunId = Guid.NewGuid(),
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = scenario,
            RunMode = "fresh-process",
            Status = status,
            CompletedAtUtc = now,
            StartedAtUtc = now.AddMilliseconds(-totalDuration),
            FixtureSha256 = fixtureSha256,
            RequestedProvider = provider,
            ActualProvider = provider,
            RequestedModel = "mock-model",
            ActualModel = "mock-model",
            TimingsMilliseconds = timings,
            MemoryBytes = memory,
            Stages = stages,
            Configuration = new Dictionary<string, string>
            {
                ["threads"] = "8",
                ["batch_size"] = "1",
            },
        };
    }

    /// <summary>
    /// Builds a ControlledStageBenchmarkMatrixReport containing per-stage evidence.
    /// </summary>
    public static ControlledStageBenchmarkMatrixReport CreateMockStageMatrixReport(
        string fixturePath,
        string provider,
        IReadOnlyDictionary<string, double> stageDurations)
    {
        DateTimeOffset startedAt = DateTimeOffset.UtcNow.AddSeconds(-2);
        DateTimeOffset completedAt = DateTimeOffset.UtcNow;

        var results = stageDurations.Select(kvp =>
        {
            var singleStageDurations = new Dictionary<string, double> { [kvp.Key] = kvp.Value };
            BenchmarkEvidenceReport evidence = CreateMockEvidenceReport(
                scenario: kvp.Key,
                provider: provider,
                stageDurations: singleStageDurations);

            return new ControlledStageBenchmarkMatrixResult(kvp.Key, evidence);
        }).ToList();

        return new ControlledStageBenchmarkMatrixReport
        {
            FixturePath = fixturePath,
            Results = results,
            Status = BenchmarkEvidenceStatus.Completed,
            StartedAtUtc = startedAt,
            CompletedAtUtc = completedAt,
            ReportPath = Path.Combine(Path.GetTempPath(), $"mock_stage_matrix_{Guid.NewGuid():N}.json"),
        };
    }

    public void Dispose()
    {
        foreach (string file in _tempFiles)
        {
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // Best effort cleanup in tests
            }
            catch (UnauthorizedAccessException)
            {
                // Best effort cleanup in tests
            }
        }
        _tempFiles.Clear();
    }
}
