#if WINDOWS
using Trackdub.Composition.HardwareProfiler;
using Trackdub.Contracts;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
using Trackdub.Inference.Runtime.Planning;
using Trackdub.TestDoubles;

namespace Trackdub.Composition.Tests.HardwareProfiler;

public sealed class TranslationBenchmarkSmokeTests
{
    /// <summary>
    /// Exercises <see cref="HardwareProfilerService.RunBenchmarkSuiteAsync"/> end-to-end while
    /// <see cref="TranslationOnlyBenchmarkRunner"/> stubs VAD/ASR/TTS ONNX runs. Only translation
    /// model paths execute the real <see cref="OnnxModelBenchmarkRunner"/>.
    /// </summary>
    [TranslationOpusSmokeFact]
    public async Task RunBenchmarkSuiteAsync_RecordsCompletedTranslationScenario()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Composition.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);

        try
        {
            var storagePaths = new TempAppStoragePaths(tempRoot);
            var settingsService = new FakeStudioSettingsService();
            var historyRecorder = new HardwareProfilerHistoryRecorder(new NoOpUserBenchmarkRepository(), storagePaths);
            var hardwareProfileProvider = new FakeHardwareProfileProvider
            {
                Profile = new HardwareProfile("Windows", "x64", HasGpu: true, GpuDescription: "Test GPU")
            };

            var service = new HardwareProfilerService(
                hardwareProfileProvider,
                BenchmarkModelPathResolver.CreateDefault(),
                new JsonHardwareProfilerStore(storagePaths),
                historyRecorder,
                settingsService,
                storagePaths,
                new TranslationOnlyBenchmarkRunner(new OnnxModelBenchmarkRunner()));

            HardwareProfilerRunResult result = await service.RunBenchmarkSuiteAsync(TestContext.Current.CancellationToken);

            Assert.True(result.Succeeded, result.ErrorMessage);
            Assert.NotNull(result.Snapshot);

            StageBenchmarkScenarioResult translation = Assert.Single(
                result.Snapshot.Scenarios,
                scenario => scenario.Scenario == StageBenchmarkScenario.Translation);

            Assert.Equal(BenchmarkStatus.Completed, translation.Status);
            Assert.Null(translation.FailureReason);
            Assert.NotNull(translation.WarmLatencyAverageMilliseconds);
            Assert.NotEqual("stub", translation.SelectedProvider);

            foreach (StageBenchmarkScenarioResult other in result.Snapshot.Scenarios)
            {
                if (other.Scenario == StageBenchmarkScenario.Translation)
                {
                    continue;
                }

                if (other.Status == BenchmarkStatus.Completed)
                {
                    Assert.Equal("stub", other.SelectedProvider);
                }
            }
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class NoOpUserBenchmarkRepository : IUserBenchmarkRepository
    {
        public Task AddAsync(BenchmarkRunRecord run, CancellationToken cancellationToken = default) =>
            Task.CompletedTask;

        public Task<bool> ContainsEvidenceAsync(Guid evidenceId, CancellationToken cancellationToken = default) =>
            Task.FromResult(false);

        public Task<IReadOnlyList<BenchmarkRunRecord>> ListByEvidenceIdAsync(
            Guid evidenceId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkRunRecord>>([]);

        public Task<IReadOnlyList<BenchmarkRunRecord>> ListRecentAsync(
            int limit,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<BenchmarkRunRecord>>([]);
    }

    /// <summary>
    /// Runs real ONNX benchmarks only for translation model paths; other stage models are stubbed
    /// so this smoke test does not depend on VAD/ASR/TTS artifacts.
    /// </summary>
    private sealed class TranslationOnlyBenchmarkRunner(OnnxModelBenchmarkRunner inner) : IModelBenchmarkRunner
    {
        public Task<BenchmarkReport> RunAsync(BenchmarkRequest request, CancellationToken cancellationToken)
        {
            if (!IsTranslationModelPath(request.ModelPath))
            {
                cancellationToken.ThrowIfCancellationRequested();
                return Task.FromResult(CreateStubReport(request));
            }

            return inner.RunAsync(request, cancellationToken);
        }

        private static bool IsTranslationModelPath(string modelPath) =>
            modelPath.Contains("opus", StringComparison.OrdinalIgnoreCase) ||
            modelPath.Contains("madlad", StringComparison.OrdinalIgnoreCase) ||
            modelPath.Contains("helsinki", StringComparison.OrdinalIgnoreCase);

        private static BenchmarkReport CreateStubReport(BenchmarkRequest request) =>
            new(
                Scenario: "translation-smoke-stub",
                ModelPath: request.ModelPath,
                ReportPath: request.ReportPath,
                Status: BenchmarkStatus.Completed,
                RequestedProvider: "stub",
                SelectedProvider: "stub",
                RunCount: request.RunCount,
                SupportsExecution: false,
                ModelSizeBytes: 0,
                Measurements: new BenchmarkMeasurements(
                    ColdLoadMilliseconds: 0,
                    WarmupMilliseconds: 0,
                    WarmLatencyAverageMilliseconds: 1,
                    WarmLatencyMinimumMilliseconds: 1,
                    WarmLatencyMaximumMilliseconds: 1,
                    AudioDurationSeconds: null,
                    RealTimeFactorAverage: null),
                FailureReason: null,
                Notes: ["Skipped non-translation stage in translation benchmark smoke test."],
                GeneratedAtUtc: DateTimeOffset.UtcNow);
    }

    private sealed class TempAppStoragePaths(string userDataRoot) : IAppStoragePaths
    {
        public string RootDirectory { get; } = userDataRoot;
        public string UserDataRoot { get; } = userDataRoot;
        public string UserCacheRoot { get; } = Path.Combine(userDataRoot, "cache");
        public string? SharedAssetRoot { get; } = null;
        public bool IsPortable { get; } = false;
        public string ModelCacheDirectory { get; } = Path.Combine(userDataRoot, "cache", "models");
        public string ModelCacheIndexPath { get; } = Path.Combine(userDataRoot, "cache", "models", "index.json");
        public string LogFilePath { get; } = Path.Combine(userDataRoot, "trackdub.log");
        public string SettingsPath { get; } = Path.Combine(userDataRoot, "settings.json");
        public string LayoutPath { get; } = Path.Combine(userDataRoot, "layout.json");
        public string ToolCacheDirectory { get; } = Path.Combine(userDataRoot, "tools");
        public string FfmpegToolCacheDirectory { get; } = Path.Combine(userDataRoot, "tools", "ffmpeg");
        public string EngineCacheDirectory { get; } = Path.Combine(userDataRoot, "cache", "engines");
        public string ComponentCacheDirectory { get; } = Path.Combine(userDataRoot, "cache", "components");
    }
}
#endif
