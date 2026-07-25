using Trackdub.Application.Dubbing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// Tests for <see cref="BatchProcessor"/> focusing on file-not-found and error-handling
/// behavior. Engine-dependent tests (full pipeline) are covered by integration/smoke tests
/// since <see cref="TrackdubDubbingEngine"/> is sealed and <see cref="TrackdubSessionFactory"/>
/// has an internal constructor.
///
/// These tests exercise:
/// - Fail-fast halt on first missing file (remaining marked Skipped)
/// - Continue-on-error with multiple missing files
/// - Report count accuracy
/// - Empty file list handling
/// </summary>
public sealed class BatchProcessorTests : IDisposable
{
    private readonly string _tempDir;
    private readonly BatchProcessor _processor;

    public BatchProcessorTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"trackdub-batch-proc-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);

        // We need a real engine instance. Use TrackdubBuilder to create a minimal one.
        // The engine will never actually execute because all files will be non-existent
        // (BatchProcessor checks File.Exists before calling engine.ExecuteAsync).
        using var factory = new TrackdubBuilder().Build();
        _processor = new BatchProcessor(new TrackdubDubbingEngine(factory));
    }

    public void Dispose()
    {
        if (Directory.Exists(_tempDir))
        {
            Directory.Delete(_tempDir, recursive: true);
        }
    }

    private static DubbingSessionOptions CreateTemplateOptions() => new()
    {
        SourceMediaPath = "placeholder.mp4",
        TargetLanguageCode = "es",
    };

    // ─── Fail-fast: first file not found, rest skipped ──────────────────────────

    [Fact]
    public async Task ExecuteAsync_FailFast_FirstFileNotFound_RemainingSkipped()
    {
        var files = new[]
        {
            Path.Combine(_tempDir, "missing1.mp4"),
            Path.Combine(_tempDir, "missing2.mp4"),
            Path.Combine(_tempDir, "missing3.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(2, report.SkippedCount);

        // First file = Failed
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Contains("not found", report.Files[0].Reason, StringComparison.OrdinalIgnoreCase);

        // Remaining = Skipped
        Assert.Equal(BatchFileStatus.Skipped, report.Files[1].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[2].Status);
    }

    [Fact]
    public async Task ExecuteAsync_FailFast_SingleFileNotFound_ReportsCorrectly()
    {
        var files = new[] { Path.Combine(_tempDir, "only-one-missing.mp4") };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Single(report.Files);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.NotNull(report.Files[0].Reason);
    }

    // ─── Continue-on-error: all files not found ─────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ContinueOnError_AllFilesNotFound_AllFailed()
    {
        var files = new[]
        {
            Path.Combine(_tempDir, "a-missing.mp4"),
            Path.Combine(_tempDir, "b-missing.mp4"),
            Path.Combine(_tempDir, "c-missing.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = true };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(3, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);

        // All three should be Failed (not Skipped) because continue-on-error
        Assert.All(report.Files, f =>
        {
            Assert.Equal(BatchFileStatus.Failed, f.Status);
            Assert.NotNull(f.Reason);
            Assert.Contains("not found", f.Reason, StringComparison.OrdinalIgnoreCase);
        });
    }

    // ─── Continue-on-error: mix of missing files ────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ContinueOnError_MultipleFailures_NoneSkipped()
    {
        var files = new[]
        {
            Path.Combine(_tempDir, "x-missing.mp4"),
            Path.Combine(_tempDir, "y-missing.wav"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = true };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(2, report.Files.Count);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(2, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
    }

    // ─── Report counts ──────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_ReportCounts_SumToTotalFileCount()
    {
        var files = new[]
        {
            Path.Combine(_tempDir, "f1.mp4"),
            Path.Combine(_tempDir, "f2.mp4"),
            Path.Combine(_tempDir, "f3.mp4"),
            Path.Combine(_tempDir, "f4.mp4"),
            Path.Combine(_tempDir, "f5.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        int total = report.SucceededCount + report.FailedCount + report.SkippedCount;
        Assert.Equal(files.Length, total);
        Assert.Equal(files.Length, report.Files.Count);
    }

    // ─── File paths preserved in report ─────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FilePathsPreserved_InReport()
    {
        var files = new[]
        {
            Path.Combine(_tempDir, "alpha.mp4"),
            Path.Combine(_tempDir, "beta.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = true };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Equal(files[0], report.Files[0].FilePath);
        Assert.Equal(files[1], report.Files[1].FilePath);
    }

    // ─── Empty file list ────────────────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_EmptyFileList_ReturnsEmptyReport()
    {
        var files = Array.Empty<string>();
        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        Assert.Empty(report.Files);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(0, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
    }

    // ─── Null argument validation ───────────────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_NullMediaFiles_ThrowsArgumentNullException()
    {
        var batchOptions = new BatchOptions { ContinueOnError = false };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ExecuteAsync(null!, CreateTemplateOptions(), batchOptions, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NullTemplateOptions_ThrowsArgumentNullException()
    {
        var files = new[] { "file.mp4" };
        var batchOptions = new BatchOptions { ContinueOnError = false };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ExecuteAsync(files, null!, batchOptions, null, CancellationToken.None));
    }

    [Fact]
    public async Task ExecuteAsync_NullBatchOptions_ThrowsArgumentNullException()
    {
        var files = new[] { "file.mp4" };

        await Assert.ThrowsAsync<ArgumentNullException>(() =>
            _processor.ExecuteAsync(files, CreateTemplateOptions(), null!, null, CancellationToken.None));
    }

    // ─── Fail-fast with second file missing ─────────────────────────────────────

    [Fact]
    public async Task ExecuteAsync_FailFast_SkippedFilesHaveReasonString()
    {
        var files = new[]
        {
            Path.Combine(_tempDir, "first-missing.mp4"),
            Path.Combine(_tempDir, "second-would-skip.mp4"),
        };

        var batchOptions = new BatchOptions { ContinueOnError = false };

        var report = await _processor.ExecuteAsync(
            files, CreateTemplateOptions(), batchOptions, progress: null, CancellationToken.None);

        // Skipped files get a reason explaining the skip
        var skipped = report.Files[1];
        Assert.Equal(BatchFileStatus.Skipped, skipped.Status);
        Assert.NotNull(skipped.Reason);
        Assert.Contains("fail-fast", skipped.Reason, StringComparison.OrdinalIgnoreCase);
    }

    // ─── Foreign-token OperationCanceledException must not escape ───────────────

    [Fact]
    public async Task ExecuteAsync_ForeignTokenOperationCanceled_DoesNotEscape_ReturnsReport()
    {
        // A cancellation token that is NOT the batch token, and is never requested.
        using var foreignCts = new CancellationTokenSource();
        var engine = new ThrowingEngine(new OperationCanceledException(foreignCts.Token));
        var processor = new BatchProcessor(engine);

        string existingFile = Path.Combine(_tempDir, "exists.mp4");
        File.WriteAllBytes(existingFile, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);

        // ct is None (not requested), so the OCE with the foreign token matches the
        // generic Exception handler's guard negatively and must hit the OCE fallback.
        BatchReport report = await processor.ExecuteAsync(
            [existingFile], CreateTemplateOptions(), new BatchOptions { ContinueOnError = false },
            progress: null, CancellationToken.None);

        Assert.Single(report.Files);
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(0, report.SucceededCount);
        Assert.Equal(1, report.FailedCount);
    }

    [Fact]
    public async Task ExecuteAsync_ForeignTokenOperationCanceled_ContinueOnError_AllFailed()
    {
        using var foreignCts = new CancellationTokenSource();
        var engine = new ThrowingEngine(new OperationCanceledException(foreignCts.Token));
        var processor = new BatchProcessor(engine);

        var files = new[]
        {
            Path.Combine(_tempDir, "a.mp4"),
            Path.Combine(_tempDir, "b.mp4"),
            Path.Combine(_tempDir, "c.mp4"),
        };
        foreach (string f in files)
        {
            File.WriteAllBytes(f, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);
        }

        BatchReport report = await processor.ExecuteAsync(
            files, CreateTemplateOptions(), new BatchOptions { ContinueOnError = true },
            progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(3, report.FailedCount);
        Assert.Equal(0, report.SkippedCount);
        Assert.All(report.Files, f => Assert.Equal(BatchFileStatus.Failed, f.Status));
    }

    [Fact]
    public async Task ExecuteAsync_ForeignTokenOperationCanceled_FailFast_RemainingSkipped()
    {
        using var foreignCts = new CancellationTokenSource();
        var engine = new ThrowingEngine(new OperationCanceledException(foreignCts.Token));
        var processor = new BatchProcessor(engine);

        var files = new[]
        {
            Path.Combine(_tempDir, "first.mp4"),
            Path.Combine(_tempDir, "second.mp4"),
            Path.Combine(_tempDir, "third.mp4"),
        };
        foreach (string f in files)
        {
            File.WriteAllBytes(f, [0x00, 0x00, 0x00, 0x1C, 0x66, 0x74, 0x79, 0x70]);
        }

        BatchReport report = await processor.ExecuteAsync(
            files, CreateTemplateOptions(), new BatchOptions { ContinueOnError = false },
            progress: null, CancellationToken.None);

        Assert.Equal(3, report.Files.Count);
        Assert.Equal(1, report.FailedCount);
        Assert.Equal(2, report.SkippedCount);
        Assert.Equal(BatchFileStatus.Failed, report.Files[0].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[1].Status);
        Assert.Equal(BatchFileStatus.Skipped, report.Files[2].Status);
    }

    /// <summary>
    /// Minimal engine double that throws a fixed exception from <see cref="ExecuteAsync"/>,
    /// used to exercise <see cref="BatchProcessor"/> error handling without a real pipeline.
    /// </summary>
    private sealed class ThrowingEngine : IDubbingPipelineEngine
    {
        private readonly Exception _exception;

        public ThrowingEngine(Exception exception) => _exception = exception;

        public Task<DubbingRunResult> ExecuteAsync(
            DubbingSessionOptions options,
            IProgress<PipelineProgressEvent>? progress = null,
            CancellationToken cancellationToken = default)
        {
            throw _exception;
        }
    }
}
