using Trackdub.Composition.HardwareProfiler;
using Trackdub.Contracts;
using Trackdub.Contracts.Persistence;
using Trackdub.Domain;
using Trackdub.Inference;
using Trackdub.Inference.Onnx;
using Trackdub.TestDoubles;
using Xunit;

namespace Trackdub.Composition.Tests.HardwareProfiler;

public sealed class HardwareProfilerServiceDualWriteTests
{
    [Fact]
    public async Task RunBenchmarkSuiteAsync_WhenHistoryWriteFails_ThrowsWithoutUpdatingStudioSettings()
    {
        string tempRoot = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var storagePaths = new TestAppStoragePaths(tempRoot);
            var settingsService = new FakeStudioSettingsService();
            string? evidenceBefore = settingsService.CurrentSettings.HardwareProfilerEvidenceId;

            var repository = new ThrowingUserBenchmarkRepository(new InvalidOperationException("benchmark history write failed"));
            var historyRecorder = new HardwareProfilerHistoryRecorder(repository, storagePaths);
            var profilerStore = new JsonHardwareProfilerStore(storagePaths);

            var service = new HardwareProfilerService(
                new FakeHardwareProfileProvider(),
                BenchmarkModelPathResolver.CreateDefault(),
                profilerStore,
                historyRecorder,
                settingsService,
                storagePaths,
                new StubBenchmarkRunner());

            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.RunBenchmarkSuiteAsync());

            Assert.Equal("benchmark history write failed", exception.Message);
            Assert.True(File.Exists(Path.Combine(tempRoot, "hardware-profiler", "latest.json")));
            Assert.Equal(evidenceBefore, settingsService.CurrentSettings.HardwareProfilerEvidenceId);
        }
        finally
        {
            if (Directory.Exists(tempRoot))
            {
                Directory.Delete(tempRoot, recursive: true);
            }
        }
    }

    private sealed class ThrowingUserBenchmarkRepository(Exception failure) : IUserBenchmarkRepository
    {
        public Task AddAsync(BenchmarkRunRecord run, CancellationToken cancellationToken = default) =>
            Task.FromException(failure);

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

    private sealed class StubBenchmarkRunner : IModelBenchmarkRunner
    {
        public Task<BenchmarkReport> RunAsync(BenchmarkRequest request, CancellationToken cancellationToken) =>
            Task.FromResult(new BenchmarkReport(
                "stub",
                request.ModelPath,
                request.ReportPath,
                BenchmarkStatus.Completed,
                "auto",
                "cpu",
                request.RunCount,
                true,
                1024,
                new BenchmarkMeasurements(null, null, 10, null, null, null, null),
                null,
                [],
                DateTimeOffset.UtcNow));
    }

    private sealed class TestAppStoragePaths(string userDataRoot) : IAppStoragePaths
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
