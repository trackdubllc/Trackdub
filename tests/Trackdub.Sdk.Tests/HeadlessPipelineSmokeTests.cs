using Microsoft.Extensions.DependencyInjection;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.LipSynthesis;
using Trackdub.Domain.StageRuns;
using Trackdub.Sdk;
using Xunit;
using Xunit.Abstractions;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// End-to-end smoke test exercising the full headless dubbing pipeline
/// with real media and ONNX models.
/// Skips when smoke media or models are not available.
/// </summary>
[Trait("Category", "Integration")]
[Trait("Category", "Smoke")]
public sealed class HeadlessPipelineSmokeTests : IAsyncLifetime, IDisposable
{
    private const string ExpectedSourceLanguage = "en";
    private const string TargetLanguage = "es";
    private const double DefaultSimilarityThreshold = 0.80;
    private const string OutputDirectoryEnvironmentVariable = "TRACKDUB_SMOKE_OUTPUT_DIRECTORY";

    private string? _tempProjectDir;
    private TrackdubSessionFactory? _factory;
    private readonly ITestOutputHelper _output;

    public HeadlessPipelineSmokeTests(ITestOutputHelper output)
    {
        _output = output;
    }

    private static bool KeepOutput =>
        string.Equals(
            Environment.GetEnvironmentVariable("TRACKDUB_SMOKE_KEEP_OUTPUT"),
            "1", StringComparison.Ordinal);

    private static TimeSpan TestTimeout =>
        TimeSpan.FromSeconds(
            int.TryParse(
                Environment.GetEnvironmentVariable("TRACKDUB_SMOKE_TIMEOUT_SECONDS"),
                out int seconds) ? seconds : 300);

    public Task InitializeAsync()
    {
        _tempProjectDir = Path.Combine(
            Path.GetTempPath(),
            $"trackdub-smoke-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempProjectDir);

        var builder = new TrackdubBuilder();
#if WINDOWS
        // TensorRT RTX cannot import several current dynamic and quantized smoke models.
        // DirectML keeps the Windows smoke GPU-accelerated without silently falling back to CPU.
        builder.WithExecutionProvider(ExecutionProviderPreference.DirectML);
#endif
        _factory = builder.Build();

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _factory?.Dispose();
        _factory = null;

        if (!KeepOutput)
        {
            TryDeleteDirectory(_tempProjectDir);
        }

        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (_factory is not null)
        {
            _factory.Dispose();
            _factory = null;
        }

        if (!KeepOutput)
        {
            TryDeleteDirectory(_tempProjectDir);
        }
    }

    private static void TryDeleteDirectory(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup
        }
    }

    [SmokeTestFact]
    public async Task FullPipeline_ProducesExportAndPassesRoundTripVerification()
    {
        string smokeMediaPath = HeadlessPipelineSmokeMedia.GetSourceMediaPath();
        if (!File.Exists(smokeMediaPath))
        {
            // Fallback runtime guard if discovery-time check was insufficient
            Assert.Fail($"Smoke media not found at: {smokeMediaPath}");
            return;
        }

        // Create engine and session
        var engine = new TrackdubDubbingEngine(_factory!);
        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = cts.Token;

        // Create session for the smoke test project
        using TrackdubSession session = _factory!.CreateSession(_tempProjectDir!);

        // Model readiness pre-flight check
        string? skipReason = await CheckModelReadinessAsync(session, ct);
        if (skipReason is not null)
        {
            _output.WriteLine($"SKIP: {skipReason}");
            return;
        }

        // Execute full pipeline
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = smokeMediaPath,
            ProjectOutputDirectory = _tempProjectDir!,
            SourceLanguageCode = null,        // force auto-detect
            TargetLanguageCode = TargetLanguage,
            ForceRerun = true,
        };

        DubbingRunResult result = await engine.ExecuteAsync(options, progress: null, ct);

        // Log execution snapshot (model/EP selections per stage)
        if (result.ExecutionSnapshot is not null)
        {
            _output.WriteLine("=== Execution Snapshot ===");
            foreach ((string key, string value) in result.ExecutionSnapshot.OrderBy(kv => kv.Key))
            {
                _output.WriteLine($"  {key} = {value}");
            }
        }

        // Log per-stage outcomes with timing
        _output.WriteLine("=== Stage Outcomes ===");
        foreach (StageOutcome stage in result.StageOutcomes)
        {
            _output.WriteLine(
                $"  {stage.StageName}: {stage.Status} ({(stage.EndTime - stage.StartTime).TotalSeconds:F1}s)" +
                (stage.ReasonCode is not null ? $" reason={stage.ReasonCode}" : ""));
            if (stage.DegradationRecords is { Count: > 0 })
            {
                foreach (string degradation in stage.DegradationRecords)
                {
                    _output.WriteLine($"    [degradation] {degradation}");
                }
            }
        }

        // Assert overall pipeline success
        if (result.OverallStatus != DubbingRunStatus.Succeeded)
        {
            var failedStages = result.StageOutcomes
                .Where(s => s.Status == StageStatus.Failed)
                .Select(s =>
                {
                    string detail = s.ReasonCode ?? "unknown";
                    if (s.DegradationRecords is { Count: > 0 })
                    {
                        detail += $" ({string.Join("; ", s.DegradationRecords)})";
                    }
                    return $"{s.StageName}: {detail}";
                })
                .ToList();
            Assert.Fail(
                $"Pipeline failed with status {result.OverallStatus}. " +
                $"Failed stages: {string.Join(", ", failedStages)}. " +
                $"Pre-flight failures: {string.Join(", ", result.PreFlightFailures ?? [])}");
        }

        // === Language detection assertion ===
        var state = await session.Workspace.Project.OpenAsync(ct);
        bool hasTranscriptSegments = state.TranscriptSegments.Count > 0;
        if (hasTranscriptSegments)
        {
            Assert.Equal(ExpectedSourceLanguage, state.TranscriptLanguage);
        }

        // === ASR transcription assertion ===
        if (hasTranscriptSegments)
        {
            Assert.All(state.TranscriptSegments, segment =>
                Assert.False(string.IsNullOrWhiteSpace(segment.Text),
                    "Transcript segment text should not be empty or whitespace"));
        }

        // === Diarization assertion ===
        StageOutcome? diarOutcome = result.StageOutcomes
            .FirstOrDefault(s => string.Equals(s.StageName, StageNames.Diarization, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(diarOutcome);
        Assert.Equal(StageStatus.Succeeded, diarOutcome.Status);
        if (hasTranscriptSegments)
        {
            Assert.NotEmpty(state.SpeakerTurns);
            Assert.NotEmpty(state.Speakers);
            _output.WriteLine($"  Diarization: {state.Speakers.Count} speaker(s), {state.SpeakerTurns.Count} turn(s)");
        }

        // === Translation assertion ===
        StageOutcome? translationOutcome = result.StageOutcomes
            .FirstOrDefault(s => string.Equals(s.StageName, StageNames.Translation, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(translationOutcome);
        if (hasTranscriptSegments)
        {
            Assert.Equal(StageStatus.Succeeded, translationOutcome.Status);
            Assert.All(state.TranslatedSegments, segment =>
                Assert.False(string.IsNullOrWhiteSpace(segment.Text),
                    "Translated segment text should not be empty or whitespace"));
        }
        else
        {
            Assert.Equal(StageStatus.Skipped, translationOutcome.Status);
            Assert.Equal(StageSkipReasonCodes.NoTranscriptSegments, translationOutcome.ReasonCode);
        }

        // === TTS and Export assertions ===
        StageOutcome? ttsOutcome = result.StageOutcomes
            .FirstOrDefault(s => string.Equals(s.StageName, StageNames.Tts, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(ttsOutcome);
        Assert.Equal(hasTranscriptSegments ? StageStatus.Succeeded : StageStatus.Skipped, ttsOutcome.Status);
        if (!hasTranscriptSegments)
        {
            Assert.Equal(StageSkipReasonCodes.NoTranscriptSegments, ttsOutcome.ReasonCode);
        }

        StageOutcome? exportOutcome = result.StageOutcomes
            .FirstOrDefault(s => string.Equals(s.StageName, StageNames.Export, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(exportOutcome);
        Assert.Equal(StageStatus.Succeeded, exportOutcome.Status);

        // Verify exported file exists with non-zero size
        Assert.NotEmpty(exportOutcome.ArtifactPaths);
        string exportedFilePath = Path.Combine(session.ProjectRootPath, exportOutcome.ArtifactPaths[0]);
        Assert.True(File.Exists(exportedFilePath),
            $"Exported dub file not found at: {exportedFilePath}");
        var exportFileInfo = new FileInfo(exportedFilePath);
        Assert.True(exportFileInfo.Length > 0,
            $"Exported dub file is empty: {exportedFilePath}");

        string retainedExportPath = RetainSuccessfulExport(smokeMediaPath, exportedFilePath);
        Assert.True(File.Exists(retainedExportPath),
            $"Retained dub file not found at: {retainedExportPath}");
        _output.WriteLine($"=== Retained dub: {retainedExportPath} ({exportFileInfo.Length} bytes) ===");

        if (KeepOutput)
        {
            _output.WriteLine($"=== Output kept at: {_tempProjectDir} ===");
            _output.WriteLine($"  Exported dub: {exportedFilePath} ({exportFileInfo.Length} bytes)");
        }

        if (!hasTranscriptSegments)
        {
            _output.WriteLine("=== No transcript segments detected; verified no-op export retention. ===");
            return;
        }

        // === Dub round-trip verification ===
        string originalTranslation = string.Join(" ",
            state.TranslatedSegments
                .OrderBy(s => s.SegmentIndex)
                .Select(s => s.Text));

        string verifyTempDir = Path.Combine(
            Path.GetTempPath(),
            $"trackdub-smoke-verify-{Guid.NewGuid():N}");
        Directory.CreateDirectory(verifyTempDir);

        try
        {
            using TrackdubSession verifySession = _factory!.CreateSession(verifyTempDir);

            var verifyOptions = new DubbingSessionOptions
            {
                SourceMediaPath = exportedFilePath,
                ProjectOutputDirectory = verifyTempDir,
                SourceLanguageCode = TargetLanguage,
                TargetLanguageCode = "en",
                StageFilter = [StageNames.Separation, StageNames.Vad, StageNames.Asr],
                ForceRerun = true,
            };

            DubbingRunResult verifyResult = await engine.ExecuteAsync(verifyOptions, progress: null, ct);

            Assert.True(
                verifyResult.OverallStatus == DubbingRunStatus.Succeeded ||
                verifyResult.OverallStatus == DubbingRunStatus.PartialSuccess,
                $"Verification ASR run failed with status: {verifyResult.OverallStatus}");

            TranscriptProjectState verifyState = await verifySession.Workspace.Project.OpenAsync(ct);
            string dubTranscription = string.Join(" ",
                verifyState.TranscriptSegments
                    .OrderBy(s => s.SegmentIndex)
                    .Select(s => s.Text));

            double similarity = TextSimilarity.WordLevel(originalTranslation, dubTranscription);
            Assert.True(
                similarity >= DefaultSimilarityThreshold,
                $"Dub round-trip similarity {similarity:P0} below threshold {DefaultSimilarityThreshold:P0}.\n" +
                $"Expected (translation): {originalTranslation}\n" +
                $"Actual (dub ASR): {dubTranscription}");
        }
        finally
        {
            if (!KeepOutput)
            {
                TryDeleteDirectory(verifyTempDir);
            }
            else
            {
                _output.WriteLine($"  Verify project kept at: {verifyTempDir}");
            }
        }
    }

    /// <summary>
    /// Extended smoke test that includes lip sync alignment and lip synthesis before export.
    /// Exercises: full pipeline → lip sync → lip synthesis → re-export with lip-synced video.
    /// Requires pipeline and lip sync/synthesis models when the smoke fixture is present.
    /// </summary>
    [SmokeTestFact]
    [Trait("Category", "LipSync")]
    public async Task FullPipelineWithLipSync_ProducesLipSyncedExport()
    {
        string smokeMediaPath = HeadlessPipelineSmokeMedia.GetSourceMediaPath();
        if (!File.Exists(smokeMediaPath))
        {
            Assert.Fail($"Smoke media not found at: {smokeMediaPath}");
            return;
        }

        // Create engine and session
        var engine = new TrackdubDubbingEngine(_factory!);
        using var cts = new CancellationTokenSource(TestTimeout);
        CancellationToken ct = cts.Token;

        using TrackdubSession session = _factory!.CreateSession(_tempProjectDir!);

        // Model readiness pre-flight check
        string? skipReason = await CheckModelReadinessAsync(session, ct);
        if (skipReason is not null)
        {
            _output.WriteLine($"SKIP: {skipReason}");
            return;
        }

        // Step 1: Run full pipeline INCLUDING export (lip synthesis needs the dubbed audio mix)
        var options = new DubbingSessionOptions
        {
            SourceMediaPath = smokeMediaPath,
            ProjectOutputDirectory = _tempProjectDir!,
            SourceLanguageCode = null,
            TargetLanguageCode = TargetLanguage,
            ForceRerun = true,
        };

        DubbingRunResult fullResult = await engine.ExecuteAsync(options, progress: null, ct);

        if (fullResult.OverallStatus != DubbingRunStatus.Succeeded)
        {
            var failedStages = fullResult.StageOutcomes
                .Where(s => s.Status == StageStatus.Failed)
                .Select(s => $"{s.StageName}: {s.ReasonCode ?? "unknown"}")
                .ToList();
            Assert.Fail(
                $"Pipeline failed with status {fullResult.OverallStatus}. " +
                $"Failed stages: {string.Join(", ", failedStages)}");
        }

        _output.WriteLine("=== Full pipeline (incl. export) succeeded ===");

        // Step 2: Run lip sync alignment (phoneme timing)
        _output.WriteLine("Running lip sync alignment...");
        Console.WriteLine("[SMOKE] Running lip sync alignment...");
        string? lipSyncFailure = null;
        bool lipSyncAligned = false;
        try
        {
            var lipSyncRequest = new Trackdub.Application.LipSync.LipSyncAlignAllRequest();
            TranscriptProjectState lipSyncState = await session.Workspace.RunLipSyncAsync(lipSyncRequest, ct);
            _output.WriteLine($"  Lip sync completed. Segments: {lipSyncState.TranscriptSegments.Count}");
            Console.WriteLine($"[SMOKE]   Lip sync completed. Segments: {lipSyncState.TranscriptSegments.Count}");

            if (lipSyncState.LipSyncSegmentStates is { Count: > 0 })
            {
                foreach (LipSyncSegmentState segState in lipSyncState.LipSyncSegmentStates)
                {
                    string detail = $"    Segment {segState.SegmentIndex}: {segState.Status}" +
                        (segState.SkipReason is not null ? $" skip={segState.SkipReason}" : "") +
                        (segState.FailureReason is not null ? $" failure={segState.FailureReason}" : "");
                    _output.WriteLine(detail);
                    Console.WriteLine($"[SMOKE] {detail}");
                }

                lipSyncAligned = lipSyncState.LipSyncSegmentStates.Any(static segState =>
                    segState.Status is LipSyncSegmentStatus.Aligned or LipSyncSegmentStatus.Partial);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lipSyncFailure = $"{ex.GetType().Name}: {ex.Message}";
            _output.WriteLine($"  Lip sync skipped/failed: {lipSyncFailure}");
            Console.WriteLine($"[SMOKE]   Lip sync EXCEPTION: {lipSyncFailure}");
            Console.WriteLine($"[SMOKE]   Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()}");
        }

        // Step 3: Run lip synthesis (video mouth repair — needs exported dubbed audio)
        _output.WriteLine("Running lip synthesis...");
        Console.WriteLine("[SMOKE] Running lip synthesis...");
        string? lipSynthesisFailure = null;
        int synthesizedSegmentCount = 0;
        try
        {
            var lipSynthesisRequest = new Trackdub.Application.LipSynthesis.LipSynthesisRunRequest(
                IsLicenseApproved: true,
                AllowExperimentalExecution: true);
            TranscriptProjectState lipSynthesisState = await session.Workspace.RunLipSynthesisAsync(lipSynthesisRequest, ct);

            if (lipSynthesisState.LipSynthesisSegmentStates is { Count: > 0 })
            {
                _output.WriteLine($"  Lip synthesis completed. Segment states: {lipSynthesisState.LipSynthesisSegmentStates.Count}");
                Console.WriteLine($"[SMOKE]   Lip synthesis completed. States: {lipSynthesisState.LipSynthesisSegmentStates.Count}");
                foreach (LipSynthesisSegmentUiState segState in lipSynthesisState.LipSynthesisSegmentStates)
                {
                    string detail = $"    Segment {segState.SegmentIndex}: {segState.Status}" +
                        (segState.SkipReason is not null ? $" skip={segState.SkipReason}" : "") +
                        (segState.FailureReason is not null ? $" failure={segState.FailureReason}" : "") +
                        (segState.ProviderId is not null ? $" provider={segState.ProviderId}" : "") +
                        (segState.ModelId is not null ? $" model={segState.ModelId}" : "");
                    _output.WriteLine(detail);
                    Console.WriteLine($"[SMOKE] {detail}");
                    if (segState.Status == LipSynthesisSegmentStatus.Synthesized)
                        synthesizedSegmentCount++;
                }
            }
            else
            {
                _output.WriteLine("  Lip synthesis completed (no segment states reported).");
                Console.WriteLine("[SMOKE]   Lip synthesis completed (no segment states reported).");
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            lipSynthesisFailure = $"{ex.GetType().Name}: {ex.Message}";
            _output.WriteLine($"  Lip synthesis skipped/failed: {lipSynthesisFailure}");
            Console.WriteLine($"[SMOKE]   Lip synthesis EXCEPTION: {lipSynthesisFailure}");
            Console.WriteLine($"[SMOKE]   Stack: {ex.StackTrace?.Split('\n').FirstOrDefault()}");
        }

        if (synthesizedSegmentCount == 0)
        {
            Assert.Fail(
                "Lip-sync smoke requires at least one synthesized lip-synthesis segment before claiming a lip-synced export. " +
                $"lipSyncAligned={lipSyncAligned}, synthesizedSegmentCount={synthesizedSegmentCount}" +
                (lipSyncFailure is not null ? $", lipSyncError={lipSyncFailure}" : "") +
                (lipSynthesisFailure is not null ? $", lipSynthesisError={lipSynthesisFailure}" : ""));
            return;
        }

        // Step 4: Re-export with lip-synced video (lip synthesis produced artifacts)
        _output.WriteLine($"Running re-export (synthesizedSegmentCount={synthesizedSegmentCount})...");
        Console.WriteLine($"[SMOKE] Running re-export (synthesizedSegmentCount={synthesizedSegmentCount})...");
        var reExportOptions = new DubbingSessionOptions
        {
            SourceMediaPath = smokeMediaPath,
            ProjectOutputDirectory = _tempProjectDir!,
            SourceLanguageCode = null,
            TargetLanguageCode = TargetLanguage,
            StageFilter = [StageNames.Export],
            ForceRerun = true,
        };

        DubbingRunResult exportResult = await engine.ExecuteAsync(reExportOptions, progress: null, ct);

        // Log execution snapshot
        if (exportResult.ExecutionSnapshot is not null)
        {
            _output.WriteLine("=== Export Execution Snapshot ===");
            foreach ((string key, string value) in exportResult.ExecutionSnapshot.OrderBy(kv => kv.Key))
            {
                _output.WriteLine($"  {key} = {value}");
            }
        }

        Assert.True(
            exportResult.OverallStatus == DubbingRunStatus.Succeeded ||
            exportResult.OverallStatus == DubbingRunStatus.PartialSuccess,
            $"Export failed with status: {exportResult.OverallStatus}");

        StageOutcome? exportOutcome = exportResult.StageOutcomes
            .FirstOrDefault(s => string.Equals(s.StageName, StageNames.Export, StringComparison.OrdinalIgnoreCase));
        Assert.NotNull(exportOutcome);
        Assert.Equal(StageStatus.Succeeded, exportOutcome.Status);

        Assert.NotEmpty(exportOutcome.ArtifactPaths);
        string exportedFilePath = Path.Combine(session.ProjectRootPath, exportOutcome.ArtifactPaths[0]);
        Assert.True(File.Exists(exportedFilePath),
            $"Exported dub file not found at: {exportedFilePath}");
        var exportFileInfo = new FileInfo(exportedFilePath);
        Assert.True(exportFileInfo.Length > 0,
            $"Exported dub file is empty: {exportedFilePath}");

        string retainedExportPath = RetainSuccessfulExport(smokeMediaPath, exportedFilePath);
        Assert.True(File.Exists(retainedExportPath),
            $"Retained lip-synced dub file not found at: {retainedExportPath}");

        _output.WriteLine($"=== Lip-synced export produced: {exportedFilePath} ({exportFileInfo.Length} bytes) ===");
        _output.WriteLine($"=== Retained lip-synced dub: {retainedExportPath} ({exportFileInfo.Length} bytes) ===");

        if (KeepOutput)
        {
            _output.WriteLine($"=== Output kept at: {_tempProjectDir} ===");
        }
    }

    private static string RetainSuccessfulExport(string sourceMediaPath, string exportedFilePath)
    {
        string outputRoot = Environment.GetEnvironmentVariable(OutputDirectoryEnvironmentVariable)
            ?? Path.Combine(FindRepositoryRoot(), "TestResults", "HeadlessPipelineSmoke");
        string sourceName = Path.GetFileNameWithoutExtension(sourceMediaPath);
        string runDirectory = Path.Combine(
            outputRoot,
            $"{sourceName}-{DateTimeOffset.UtcNow:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(runDirectory);

        string retainedExportPath = Path.Combine(runDirectory, Path.GetFileName(exportedFilePath));
        File.Copy(exportedFilePath, retainedExportPath, overwrite: false);

        return retainedExportPath;
    }

    private static string FindRepositoryRoot()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
             directory is not null;
             directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Trackdub.slnx")))
            {
                return directory.FullName;
            }
        }

        throw new DirectoryNotFoundException("Could not locate Trackdub repository root for smoke-test output.");
    }

    /// <summary>
    /// Checks whether pipeline models are available. Returns a skip reason if not.
    /// </summary>
    private async Task<string?> CheckModelReadinessAsync(TrackdubSession session, CancellationToken ct)
    {
        var checker = session.GetServiceProvider()?.GetService<IPipelinePreFlightChecker>();

        if (checker is null)
            return null;

        string[] stages =
        [
            StageNames.Separation,
            StageNames.Vad,
            StageNames.Diarization,
            StageNames.Asr,
            StageNames.Translation,
            StageNames.Tts,
            StageNames.Export,
        ];

        var missingModels = new List<string>();

        foreach (string stage in stages)
        {
            try
            {
                await checker.EnsureModelsAvailableAsync(stage, ct).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                missingModels.Add($"{stage}: {ex.Message}");
            }
        }

        return missingModels.Count > 0
            ? $"Required models not available:\n{string.Join("\n", missingModels)}"
            : null;
    }
}
