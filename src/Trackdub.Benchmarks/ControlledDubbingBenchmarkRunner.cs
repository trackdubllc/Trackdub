using System.Diagnostics;
using System.Security.Cryptography;
using Trackdub.Application.Dubbing;
using Trackdub.Benchmarks.Metrics;
using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Persistence;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Benchmarking;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Settings;
using Trackdub.Benchmarks.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Benchmarks;

/// <summary>Runs an isolated fixture through a real pipeline, preserving terminal evidence.</summary>
public sealed class ControlledDubbingBenchmarkRunner : IDisposable
{
    private readonly IBenchmarkEvidenceRepository _history;
    private readonly Action<IServiceCollection>? _serviceConfigurator;
    private HeadlessDubbingHost? _warmHost;
    private string? _warmHostKey;

    public ControlledDubbingBenchmarkRunner(
        IBenchmarkEvidenceRepository? history = null,
        Action<IServiceCollection>? serviceConfigurator = null)
    {
        _history = history ?? new BenchmarkEvidenceRepository(
            new SqliteUserBenchmarkDatabase(new TrackdubStoragePaths().UserDataRoot));
        _serviceConfigurator = serviceConfigurator;
    }

    public void Dispose()
    {
        _warmHost?.Dispose();
        _warmHost = null;
        _warmHostKey = null;
    }

    public async Task<BenchmarkEvidenceReport> RunAsync(
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        string? stage = ResolveStage(options.Stage);
        ValidateOptions(options);
        Guid reportId = Guid.NewGuid();
        DateTimeOffset startedAt = DateTimeOffset.UtcNow;
        var clock = Stopwatch.StartNew();
        var timings = new Dictionary<string, double?>(StringComparer.Ordinal)
        {
            ["hostCreation"] = null,
            ["fixturePreparation"] = null,
            ["prerequisites"] = null,
            ["warmup"] = null,
            ["preflight"] = null,
            ["pipeline"] = null,
            ["export"] = null,
            ["disposal"] = null,
            ["firstUsableTranscript"] = null,
            ["firstPlayableAudio"] = null,
        };
        var resourceTelemetry = new List<BenchmarkStageResourceTelemetry>();
        ResourceTelemetrySnapshot? processTelemetryStart = ResourceTelemetry.TryCaptureProcess();
        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = processTelemetryStart?.WorkingSetBytes,
            ["processWorkingSetEnd"] = null,
            ["processPeakWorkingSet"] = processTelemetryStart?.PeakWorkingSetBytes,
            ["peakWorkingSetBytes"] = processTelemetryStart?.PeakWorkingSetBytes,
            ["managedAllocatedBytes"] = null,
            ["gen0Collections"] = null,
            ["gen1Collections"] = null,
            ["gen2Collections"] = null,
            ["availableVramMb"] = null,
        };
        string? fixtureHash = null;
        string? reason = null;
        BenchmarkEvidenceStatus status = BenchmarkEvidenceStatus.Failed;
        IReadOnlyList<BenchmarkEvidenceStage> stages = [];
        Guid runId = reportId;
        string? actualModel = null;
        string? actualProvider = null;
        string projectRoot = Path.Join(options.OutputDirectory, "projects", reportId.ToString("N"));
        string fixtureCopy = Path.Join(projectRoot, "fixture" + Path.GetExtension(options.FixturePath));
        string projectPath = Path.Join(projectRoot, "project.trackdub");
        HeadlessDubbingHost? host = null;
        IDisposable? cacheScope = null;
        bool ownsHost = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(projectRoot);
            long preparationStart = Stopwatch.GetTimestamp();
            await using (FileStream source = File.OpenRead(options.FixturePath))
            await using (FileStream destination = File.Create(fixtureCopy))
            {
                await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
            }
            await using (FileStream hashStream = File.OpenRead(fixtureCopy))
            {
                fixtureHash = Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)
                    .ConfigureAwait(false)).ToLowerInvariant();
            }
            timings["fixturePreparation"] = Stopwatch.GetElapsedTime(preparationStart).TotalMilliseconds;
            if (options.ExpectedFixtureSha256 is not null &&
                !fixtureHash.Equals(options.ExpectedFixtureSha256, StringComparison.OrdinalIgnoreCase))
            {
                status = BenchmarkEvidenceStatus.Failed;
                reason = "Fixture checksum differs from the expected SHA-256.";
                throw new PreparationIncompleteException();
            }

            if (options.Mode == "fresh-process" && !options.ReuseEngineCache)
            {
                string cache = Path.Join(projectRoot, "engine-cache");
                Directory.CreateDirectory(cache);
                cacheScope = new EnvironmentOverride(TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable, cache);
                ((EnvironmentOverride)cacheScope).Apply();
            }

            long hostStart = Stopwatch.GetTimestamp();
            if (options.Mode == "warm-host")
            {
                string key = string.Join("|", options.ModelDirectory, options.Provider,
                    options.Stage, options.FfmpegPath, options.FfprobePath);
                if (_warmHostKey != key)
                {
                    _warmHost?.Dispose();
                    _warmHost = null;
                    _warmHostKey = key;
                }
                _warmHost ??= CreateHost(options);
                host = _warmHost;
            }
            else
            {
                host = CreateHost(options);
                ownsHost = true;
            }
            // Headless storage overrides may set this variable while building the host.
            // Apply the per-sample engine-cache root again before any session is created.
            if (cacheScope is EnvironmentOverride engineCache)
                engineCache.Apply();
            timings["hostCreation"] = Stopwatch.GetElapsedTime(hostStart).TotalMilliseconds;

            int runCount = Math.Max(1, options.RunCount);
            string baselineProjectPath = Path.Join(projectRoot, "baseline", "project.trackdub");
            bool hasPrerequisites = false;

            if (stage is not null)
            {
                IReadOnlyList<string> prerequisites = PrerequisitesFor(stage);
                if (prerequisites.Count > 0)
                {
                    hasPrerequisites = true;
                    Directory.CreateDirectory(Path.GetDirectoryName(baselineProjectPath)!);
                    long prerequisiteStart = Stopwatch.GetTimestamp();
                    DubbingRunResult preparation = await ExecuteWithTelemetryAsync(
                        baselineProjectPath, prerequisites, true, "prerequisites", 0).ConfigureAwait(false);
                    timings["prerequisites"] = Stopwatch.GetElapsedTime(prerequisiteStart).TotalMilliseconds;
                    RequirePreparationSucceeded(
                        preparation, "Prerequisite preparation did not complete successfully.",
                        out reason, out status, out stages);

                    // A stage already run by its prerequisites is timed as an in-place re-run
                    // (for ASR: re-transcribing existing segments), not as the stage itself.
                    // No prerequisite regenerates a later stage today, but guard against one
                    // sneaking the timed stage in (e.g. an opt-in stage running during import).
                    RunArtifacts prepared = await ReadRunArtifactsAsync(host, baselineProjectPath, options, cancellationToken)
                        .ConfigureAwait(false);
                    if (prepared.StageRuns.Any(run => run.StageName.Equals(stage, StringComparison.OrdinalIgnoreCase)))
                    {
                        reason = $"Prerequisites already ran '{stage}'; the timed run would measure a re-run.";
                        status = BenchmarkEvidenceStatus.Skipped;
                        throw new PreparationIncompleteException();
                    }
                }
            }

            IReadOnlyList<string>? filter = stage is null ? null : [stage];
            if (options.Mode == "warm-host")
            {
                string warmupProjectPath = Path.Join(projectRoot, "warmup", "project.trackdub");
                if (hasPrerequisites)
                {
                    CopyDirectory(baselineProjectPath, warmupProjectPath);
                }
                else
                {
                    Directory.CreateDirectory(warmupProjectPath);
                }

                long warmupStart = Stopwatch.GetTimestamp();
                DubbingRunResult warmup = await ExecuteWithTelemetryAsync(
                    warmupProjectPath, filter, true, "warmup", 0).ConfigureAwait(false);
                timings["warmup"] = Stopwatch.GetElapsedTime(warmupStart).TotalMilliseconds;
                RequirePreparationSucceeded(
                    warmup, "Warm-host preparation did not complete successfully.",
                    out reason, out status, out stages);
            }

            string? primingProjectPath = null;
            if (options.Mode == "artifact-resume")
            {
                primingProjectPath = Path.Join(projectRoot, "priming", "project.trackdub");
                if (hasPrerequisites)
                {
                    CopyDirectory(baselineProjectPath, primingProjectPath);
                }
                else
                {
                    Directory.CreateDirectory(primingProjectPath);
                }

                DubbingRunResult priming = await ExecuteWithTelemetryAsync(
                    primingProjectPath, filter, true, "priming", 0).ConfigureAwait(false);
                if (priming.OverallStatus != DubbingRunStatus.Succeeded)
                {
                    reason = "Artifact-resume preparation did not complete successfully.";
                    status = BenchmarkEvidenceStatus.Skipped;
                    stages = MapStages(priming, [], null, null, null);
                    throw new PreparationIncompleteException();
                }
            }

            var stageSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            var stageMemorySamples = new Dictionary<string, List<ResourceTelemetryDelta>>(StringComparer.OrdinalIgnoreCase);
            var pipelineSamples = new List<double>(runCount);
            var phaseSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
            DubbingRunResult lastResult = null!;
            StageTimingCollector lastClock = null!;
            string lastProjectPath = null!;

            for (int runIndex = 1; runIndex <= runCount; runIndex++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string iterProjectPath = Path.Join(projectRoot, $"run_{runIndex}", "project.trackdub");

                if (options.Mode == "artifact-resume")
                {
                    // Seed from the primed project so the measured run actually resumes/skips
                    // the already-completed stages instead of doing a full cold run.
                    CopyDirectory(primingProjectPath!, iterProjectPath);
                }
                else if (hasPrerequisites)
                {
                    CopyDirectory(baselineProjectPath, iterProjectPath);
                }
                else
                {
                    Directory.CreateDirectory(iterProjectPath);
                }

                long runStart = Stopwatch.GetTimestamp();
                var stageClock = new StageTimingCollector(runStart);
                var phases = new BenchmarkPhaseCapture();
                DubbingRunResult iterResult;
                using (BenchmarkPhaseCapture.Activate(phases))
                {
                    iterResult = await ExecuteWithTelemetryAsync(
                        iterProjectPath, filter, options.Mode != "artifact-resume",
                        "measured", runIndex, stageClock).ConfigureAwait(false);
                }

                double pipeDuration = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
                pipelineSamples.Add(pipeDuration);

                foreach ((string name, double? duration) in phases.SnapshotMilliseconds())
                {
                    if (duration is double phaseMs)
                    {
                        if (!phaseSamples.TryGetValue(name, out var list))
                        {
                            list = [];
                            phaseSamples[name] = list;
                        }

                        list.Add(phaseMs);
                    }
                }

                foreach (var outcome in iterResult.StageOutcomes)
                {
                    double? ms = stageClock.GetMilliseconds(outcome.StageName);
                    if (ms is double stageMs)
                    {
                        if (!stageSamples.TryGetValue(outcome.StageName, out var list))
                        {
                            list = [];
                            stageSamples[outcome.StageName] = list;
                        }

                        list.Add(stageMs);
                    }

                    ResourceTelemetryDelta? memDelta = stageClock.GetMemoryDelta(outcome.StageName);
                    if (memDelta is not null)
                    {
                        if (!stageMemorySamples.TryGetValue(outcome.StageName, out var memList))
                        {
                            memList = [];
                            stageMemorySamples[outcome.StageName] = memList;
                        }

                        memList.Add(memDelta);
                    }
                }

                lastResult = iterResult;
                lastClock = stageClock;
                lastProjectPath = iterProjectPath;

                if (iterResult.OverallStatus != DubbingRunStatus.Succeeded)
                {
                    break;
                }
            }

            RunArtifacts artifacts = await ReadRunArtifactsAsync(host, lastProjectPath, options, cancellationToken)
                .ConfigureAwait(false);
            double mediaDuration = artifacts.MediaDurationSeconds;

            foreach ((string stageName, List<double> samples) in stageSamples)
            {
                LatencyStatistics stageStats = PercentileCalculator.Calculate(
                    samples,
                    totalUnits: mediaDuration,
                    totalDurationSeconds: samples.Sum() / 1000.0);

                timings[$"stage:{stageName}:min"] = stageStats.MinMilliseconds;
                timings[$"stage:{stageName}:max"] = stageStats.MaxMilliseconds;
                timings[$"stage:{stageName}:mean"] = stageStats.MeanMilliseconds;
                timings[$"stage:{stageName}:p50"] = stageStats.P50Milliseconds;
                timings[$"stage:{stageName}:p90"] = stageStats.P90Milliseconds;
                timings[$"stage:{stageName}:p99"] = stageStats.P99Milliseconds;
                timings[$"stage:{stageName}:throughput"] = stageStats.ThroughputUnitsPerSecond;
                timings[$"stage:{stageName}:sampleCount"] = (double)stageStats.SampleCount;
                timings[$"stage:{stageName}"] = stageStats.P50Milliseconds;

                string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
                if (!canonical.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                {
                    timings[$"stage:{canonical}:min"] = stageStats.MinMilliseconds;
                    timings[$"stage:{canonical}:max"] = stageStats.MaxMilliseconds;
                    timings[$"stage:{canonical}:mean"] = stageStats.MeanMilliseconds;
                    timings[$"stage:{canonical}:p50"] = stageStats.P50Milliseconds;
                    timings[$"stage:{canonical}:p90"] = stageStats.P90Milliseconds;
                    timings[$"stage:{canonical}:p99"] = stageStats.P99Milliseconds;
                    timings[$"stage:{canonical}:throughput"] = stageStats.ThroughputUnitsPerSecond;
                    timings[$"stage:{canonical}:sampleCount"] = (double)stageStats.SampleCount;
                    timings[$"stage:{canonical}"] = stageStats.P50Milliseconds;
                }
            }

            if (stage is not null && !timings.ContainsKey($"stage:{stage}:p50"))
            {
                string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stage);
                if (timings.TryGetValue($"stage:{canonical}:p50", out double? canP50))
                {
                    timings[$"stage:{stage}:p50"] = canP50;
                    timings[$"stage:{stage}:min"] = timings.GetValueOrDefault($"stage:{canonical}:min");
                    timings[$"stage:{stage}:max"] = timings.GetValueOrDefault($"stage:{canonical}:max");
                    timings[$"stage:{stage}:mean"] = timings.GetValueOrDefault($"stage:{canonical}:mean");
                    timings[$"stage:{stage}:p90"] = timings.GetValueOrDefault($"stage:{canonical}:p90");
                    timings[$"stage:{stage}:p99"] = timings.GetValueOrDefault($"stage:{canonical}:p99");
                    timings[$"stage:{stage}:throughput"] = timings.GetValueOrDefault($"stage:{canonical}:throughput");
                    timings[$"stage:{stage}:sampleCount"] = timings.GetValueOrDefault($"stage:{canonical}:sampleCount");
                    timings[$"stage:{stage}"] = canP50;
                }
            }

            foreach (string stageName in stageSamples.Keys)
            {
                if (stageMemorySamples.TryGetValue(stageName, out var mSamples) && mSamples.Count > 0)
                {
                    var sortedAlloc = mSamples.Select(s => (double)s.ManagedAllocatedBytes).OrderBy(x => x).ToArray();
                    long allocated = (long)Math.Round(PercentileCalculator.CalculatePercentile(sortedAlloc, 0.5));
                    long peakWs = mSamples.Max(s => s.PeakWorkingSetBytes);
                    int gen0 = (int)Math.Round(PercentileCalculator.CalculatePercentile(mSamples.Select(s => (double)s.Gen0Collections).OrderBy(x => x).ToArray(), 0.5));
                    int gen1 = (int)Math.Round(PercentileCalculator.CalculatePercentile(mSamples.Select(s => (double)s.Gen1Collections).OrderBy(x => x).ToArray(), 0.5));
                    int gen2 = (int)Math.Round(PercentileCalculator.CalculatePercentile(mSamples.Select(s => (double)s.Gen2Collections).OrderBy(x => x).ToArray(), 0.5));

                    memory[$"stage:{stageName}:allocatedBytes"] = allocated;
                    memory[$"stage:{stageName}:peakWorkingSet"] = peakWs;
                    memory[$"stage:{stageName}:gen0"] = gen0;
                    memory[$"stage:{stageName}:gen1"] = gen1;
                    memory[$"stage:{stageName}:gen2"] = gen2;

                    string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
                    if (!canonical.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                    {
                        memory[$"stage:{canonical}:allocatedBytes"] = allocated;
                        memory[$"stage:{canonical}:peakWorkingSet"] = peakWs;
                        memory[$"stage:{canonical}:gen0"] = gen0;
                        memory[$"stage:{canonical}:gen1"] = gen1;
                        memory[$"stage:{canonical}:gen2"] = gen2;
                    }
                }
                else if (lastClock?.GetMemoryDelta(stageName) is ResourceTelemetryDelta delta)
                {
                    memory[$"stage:{stageName}:allocatedBytes"] = delta.ManagedAllocatedBytes;
                    memory[$"stage:{stageName}:peakWorkingSet"] = delta.PeakWorkingSetBytes;
                    memory[$"stage:{stageName}:gen0"] = delta.Gen0Collections;
                    memory[$"stage:{stageName}:gen1"] = delta.Gen1Collections;
                    memory[$"stage:{stageName}:gen2"] = delta.Gen2Collections;

                    string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
                    if (!canonical.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                    {
                        memory[$"stage:{canonical}:allocatedBytes"] = delta.ManagedAllocatedBytes;
                        memory[$"stage:{canonical}:peakWorkingSet"] = delta.PeakWorkingSetBytes;
                        memory[$"stage:{canonical}:gen0"] = delta.Gen0Collections;
                        memory[$"stage:{canonical}:gen1"] = delta.Gen1Collections;
                        memory[$"stage:{canonical}:gen2"] = delta.Gen2Collections;
                    }
                }
            }

            if (lastClock is not null)
            {
                foreach ((string stageKey, ResourceTelemetryDelta delta) in lastClock.GetAllMemoryDeltas())
                {
                    if (!memory.ContainsKey($"stage:{stageKey}:allocatedBytes"))
                    {
                        memory[$"stage:{stageKey}:allocatedBytes"] = delta.ManagedAllocatedBytes;
                        memory[$"stage:{stageKey}:peakWorkingSet"] = delta.PeakWorkingSetBytes;
                        memory[$"stage:{stageKey}:gen0"] = delta.Gen0Collections;
                        memory[$"stage:{stageKey}:gen1"] = delta.Gen1Collections;
                        memory[$"stage:{stageKey}:gen2"] = delta.Gen2Collections;
                    }
                }
            }

            if (pipelineSamples.Count > 0)
            {
                LatencyStatistics pipeStats = PercentileCalculator.Calculate(
                    pipelineSamples,
                    totalUnits: mediaDuration,
                    totalDurationSeconds: pipelineSamples.Sum() / 1000.0);

                timings["pipeline"] = pipeStats.P50Milliseconds;
                timings["pipeline:min"] = pipeStats.MinMilliseconds;
                timings["pipeline:max"] = pipeStats.MaxMilliseconds;
                timings["pipeline:mean"] = pipeStats.MeanMilliseconds;
                timings["pipeline:p50"] = pipeStats.P50Milliseconds;
                timings["pipeline:p90"] = pipeStats.P90Milliseconds;
                timings["pipeline:p99"] = pipeStats.P99Milliseconds;
                timings["pipeline:throughput"] = pipeStats.ThroughputUnitsPerSecond;
                timings["pipeline:sampleCount"] = (double)pipeStats.SampleCount;
            }

            foreach ((string name, List<double> pSamples) in phaseSamples)
            {
                timings[name] = PercentileCalculator.Calculate(pSamples).P50Milliseconds;
            }

            timings["import"] = timings.GetValueOrDefault("phase:import");
            timings["preflight"] = timings.GetValueOrDefault("phase:preflight");
            timings["export"] = timings.GetValueOrDefault("stage:Export:p50")
                ?? lastClock?.GetMilliseconds("Export");

            runId = lastResult.RunId;
            stages = MapStages(lastResult, artifacts.StageRuns, options.Model, stageSamples, lastClock);

            if (artifacts.HasUsableTranscript)
                timings["firstUsableTranscript"] = lastClock?.GetCompletionMilliseconds("Asr");
            if (artifacts.HasPlayableTake)
                timings["firstPlayableAudio"] = lastClock?.GetCompletionMilliseconds("Tts");

            BenchmarkEvidenceStage? requestedStage = stage is null
                ? null
                : stages.LastOrDefault(x =>
                    x.Name.Equals(stage, StringComparison.OrdinalIgnoreCase) ||
                    x.Name.Equals(MockDubbingPipelineServices.CanonicalBenchmarkStage(stage), StringComparison.OrdinalIgnoreCase) ||
                    MockDubbingPipelineServices.CanonicalBenchmarkStage(x.Name).Equals(MockDubbingPipelineServices.CanonicalBenchmarkStage(stage), StringComparison.OrdinalIgnoreCase));
            actualModel = requestedStage?.ActualModel;
            actualProvider = requestedStage?.ActualProvider;
            if (stage is not null && requestedStage?.Status != BenchmarkEvidenceStatus.Completed)
            {
                status = requestedStage?.Status ?? BenchmarkEvidenceStatus.Skipped;
                reason = requestedStage?.Reason
                    ?? (lastResult.PreFlightFailures is { Count: > 0 }
                        ? "Preflight failed: " + string.Join("; ", lastResult.PreFlightFailures)
                        : "Requested stage produced no successful outcome.");
            }
            else if (lastResult.OverallStatus != DubbingRunStatus.Succeeded)
            {
                status = lastResult.OverallStatus == DubbingRunStatus.PartialSuccess
                    ? BenchmarkEvidenceStatus.PartiallyCompleted
                    : BenchmarkEvidenceStatus.Failed;
                reason = lastResult.PreFlightFailures is { Count: > 0 }
                    ? "Preflight failed."
                    : string.Join("; ", stages
                        .Where(measured => measured.Status != BenchmarkEvidenceStatus.Completed)
                        .Select(measured => $"{measured.Name}:{measured.Reason ?? measured.Status.ToString()}"));
                if (string.IsNullOrWhiteSpace(reason))
                    reason = "Pipeline did not complete successfully.";
            }
            else if (options.Provider is not null &&
                     (actualProvider is null ||
                     !BenchmarkComparison.ProviderMatches(options.Provider, actualProvider)))
            {
                status = BenchmarkEvidenceStatus.PartiallyCompleted;
                reason = "Actual provider was unavailable or differed from requested provider.";
            }
            else
            {
                status = BenchmarkEvidenceStatus.Completed;
            }
        }
        catch (PreparationIncompleteException)
        {
            // The preparation result and reason have already been captured.
        }
        catch (OperationCanceledException)
        {
            status = BenchmarkEvidenceStatus.Canceled;
            reason = "Canceled.";
        }
        catch (Exception ex)
        {
            status = BenchmarkEvidenceStatus.Failed;
            reason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (ownsHost && host is not null)
            {
                long disposeStart = Stopwatch.GetTimestamp();
                host.Dispose();
                timings["disposal"] = Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds;
            }
            cacheScope?.Dispose();
        }
        return await FinishAsync().ConfigureAwait(false);

        async Task<BenchmarkEvidenceReport> FinishAsync()
        {
            clock.Stop();
            ResourceTelemetrySnapshot? processTelemetryEnd = ResourceTelemetry.TryCaptureProcess();
            ResourceTelemetryDelta? processDelta = processTelemetryStart is not null && processTelemetryEnd is not null
                ? ResourceTelemetry.CalculateDelta(processTelemetryStart, processTelemetryEnd) : null;

            memory["processWorkingSetEnd"] = processTelemetryEnd?.WorkingSetBytes;
            memory["processPeakWorkingSet"] = processDelta?.PeakWorkingSetBytes;
            memory["peakWorkingSetBytes"] = processDelta?.PeakWorkingSetBytes;
            memory["managedAllocatedBytes"] = processDelta?.ManagedAllocatedBytes;
            memory["gen0Collections"] = processDelta?.Gen0Collections;
            memory["gen1Collections"] = processDelta?.Gen1Collections;
            memory["gen2Collections"] = processDelta?.Gen2Collections;

            // The legacy memory map carried a permanently-null GPU placeholder; report the real
            // adapter-wide free VRAM instead. Reuses the last measured sample's reading so the
            // report costs no extra process sample.
            BenchmarkStageResourceTelemetry? lastMeasured = resourceTelemetry
                .LastOrDefault(sample => sample.Phase == "measured");
            memory["availableVramMb"] = lastMeasured is null
                ? null
                : (long?)lastMeasured.Validation.Checks
                    .FirstOrDefault(check => check.Metric == "availableVramMb")?.ObservedValue;

            if (!resourceTelemetry.Any(sample => sample.Phase == "measured"))
            {
                resourceTelemetry.Add(new()
                {
                    Stage = stage ?? "full-pipeline",
                    Phase = "measured",
                    ExecutionStatus = BenchmarkEvidenceStatus.Skipped,
                    Reason = reason ?? "No measured pipeline stages ran.",
                    Validation = new()
                    {
                        Status = ResourceTelemetryStatus.Skipped,
                        Checks = [new("stage", ResourceTelemetryStatus.Skipped, null, null,
                            reason ?? "No measured pipeline stages ran.")],
                    },
                });
            }
            string[] resourceFailures = resourceTelemetry.SelectMany(sample => sample.Validation.Checks
                .Where(check => check.Status == ResourceTelemetryStatus.Failed)
                .Select(check => $"{sample.Stage} ({sample.Phase}, iteration {sample.Iteration}, attempt {sample.Attempt}): {check.Metric}: {check.Reason}"))
                .ToArray();
            if (resourceFailures.Length > 0)
            {
                if (status != BenchmarkEvidenceStatus.Canceled) status = BenchmarkEvidenceStatus.Failed;
                reason = string.Join("; ", new[] { reason, "Resource validation failed: " + string.Join("; ", resourceFailures) }
                    .Where(text => !string.IsNullOrWhiteSpace(text)));
            }
            ResourceTelemetryStatus resourceStatus = resourceFailures.Length > 0
                ? ResourceTelemetryStatus.Failed
                : resourceTelemetry.Any(sample => sample.Validation.Status == ResourceTelemetryStatus.Unavailable)
                    ? ResourceTelemetryStatus.Unavailable
                    : resourceTelemetry.All(sample => sample.Validation.Status == ResourceTelemetryStatus.Skipped)
                        ? ResourceTelemetryStatus.Skipped : ResourceTelemetryStatus.Passed;

            timings["total"] = clock.Elapsed.TotalMilliseconds;
            var configuration = new Dictionary<string, string>
            {
                ["targetLanguage"] = options.TargetLanguage,
                ["sourceLanguage"] = options.SourceLanguage ?? "auto",
                ["stage"] = stage ?? "all",
                ["hardware"] = BenchmarkHardwareInfo.Capture(),
                ["resourceScope"] = "Process-wide; includes concurrent work; excludes child processes.",
                ["cpuNormalization"] = "100 * delta CPU milliseconds / (monotonic elapsed milliseconds * processor count)",
                ["memorySampling"] = "Maximum of stage endpoint working sets; transient peaks are not captured.",
                ["vramScope"] = "Adapter-wide free VRAM (budget minus current usage), not this process's allocation; moves with other processes on the same GPU.",
            };
            foreach (BenchmarkEvidenceStage measuredStage in stages)
            {
                if (measuredStage.ActualModel is not null)
                    configuration[$"stage:{measuredStage.Name}:model"] = measuredStage.ActualModel;
                if (measuredStage.ActualProvider is not null)
                    configuration[$"stage:{measuredStage.Name}:provider"] = measuredStage.ActualProvider;
            }
            var runtimeVersions = new Dictionary<string, string>
            {
                ["dotnet"] = Environment.Version.ToString(),
                ["os"] = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
                ["processArchitecture"] = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
            };
            foreach (System.Reflection.Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                string? name = assembly.GetName().Name;
                if (name is "Microsoft.ML.OnnxRuntime" or "Microsoft.ML.OnnxRuntimeGenAI" or
                    "Microsoft.ML.OnnxRuntimeGenAI.Managed")
                    runtimeVersions[name] = assembly.GetName().Version?.ToString() ?? "unknown";
            }
            var report = new BenchmarkEvidenceReport
            {
                RunId = runId,
                Kind = BenchmarkEvidenceKind.Benchmark,
                Scenario = stage ?? "full-pipeline",
                RunMode = options.Mode == "fresh-process"
                    ? options.ReuseEngineCache ? "fresh-process-compatible-cache" : "fresh-process-isolated-engine-cache"
                    : options.Mode,
                Status = status,
                Reason = reason,
                StartedAtUtc = startedAt,
                CompletedAtUtc = DateTimeOffset.UtcNow,
                FixtureSha256 = fixtureHash,
                RequestedModel = options.Model,
                ActualModel = actualModel,
                RequestedProvider = options.Provider,
                ActualProvider = actualProvider,
                Configuration = configuration,
                RuntimeVersions = runtimeVersions,
                TimingsMilliseconds = timings,
                MemoryBytes = memory,
                Stages = stages,
                ResourceTelemetryBounds = options.ResourceTelemetryBounds,
                ResourceValidationStatus = resourceStatus,
                ResourceTelemetry = resourceTelemetry.ToArray(),
                ResourceDistribution = ResourceTelemetryAggregator.Aggregate(resourceTelemetry),
            };
            using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            CancellationToken saveToken = status == BenchmarkEvidenceStatus.Canceled
                ? saveTimeout.Token
                : cancellationToken;
            await _history.SaveAsync(report, saveToken).ConfigureAwait(false);
            return report;
        }

        async Task<DubbingRunResult> ExecuteWithTelemetryAsync(
            string project, IReadOnlyList<string>? filter, bool forceRerun,
            string phase, int iteration, StageTimingCollector? stageClock = null)
        {
            var capture = new StageResourceTelemetryCapture(
                host!.Services.GetRequiredService<IResourceTelemetryCollector>(),
                host.Services.GetRequiredService<IResourceTelemetryValidator>(),
                options.ResourceTelemetryBounds, phase, iteration, stageClock);
            try
            {
                DubbingRunResult result = await ExecuteAsync(host, fixtureCopy, project, options,
                    filter, forceRerun, cancellationToken, capture).ConfigureAwait(false);
                capture.CompleteOutcomes(result.StageOutcomes);
                capture.CompletePending(BenchmarkEvidenceStatus.Failed, "Stage emitted no terminal outcome.");
                return result;
            }
            catch (OperationCanceledException)
            {
                capture.CompletePending(BenchmarkEvidenceStatus.Canceled, "Canceled.");
                throw;
            }
            catch (Exception ex)
            {
                capture.CompletePending(BenchmarkEvidenceStatus.Failed, $"{ex.GetType().Name}: {ex.Message}");
                throw;
            }
            finally
            {
                resourceTelemetry.AddRange(capture.Snapshot());
            }
        }
    }

    // An unknown alias is not an error to the pipeline, which silently plans its default model,
    // so a typo (or an engine family such as "whisper-onnx") would measure the wrong model.
    // A resolved alias must also match the selected stage's task: the planner treats a
    // preferred alias as a preference, not a requirement, so a mismatched-task alias would
    // let the pipeline plan its default model while the report claims the requested one.
    private static void ValidateModelAlias(string model, string stage)
    {
        if (!BundledModelManifestRegistry.TryLoadDefault(out BundledModelManifestRegistry? registry, out _) ||
            registry is null)
        {
            // Manifest unavailable: the pipeline preflight surfaces model problems later.
            return;
        }

        if (!registry.TryResolve(model, out BundledModelManifestResolution? resolution) ||
            resolution is null)
        {
            throw UnknownAliasException(model, registry);
        }

        string? requiredTask = ManifestTaskFor(RuntimeStageFor(stage));
        if (requiredTask is not null &&
            !string.Equals(resolution.Entry.Task, requiredTask, StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                $"Model alias '{model}' resolves to a {resolution.Entry.Task} model, which cannot serve the '{stage}' stage (requires {requiredTask}).");
        }
    }

    private static ArgumentException UnknownAliasException(string model, BundledModelManifestRegistry registry)
    {
        string family = model.Split('-', '_', '@')[0];
        string[] similar = registry.Entries
            .SelectMany(static entry => entry.Aliases)
            .Where(alias => family.Length > 2 && alias.StartsWith(family, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .Take(12)
            .ToArray();
        return new ArgumentException(similar.Length == 0
            ? $"Unknown model alias '{model}'."
            : $"Unknown model alias '{model}'. Similar aliases: {string.Join(", ", similar)}.");
    }

    // Mirrors the stage → manifest task mapping in StageRuntimeRequirementsCatalog
    // (Trackdub.Inference.Runtime.Planning), which is internal to that assembly.
    private static string? ManifestTaskFor(RuntimeStage? stage) => stage switch
    {
        RuntimeStage.Vad => "vad",
        RuntimeStage.Asr => "asr",
        RuntimeStage.Translation => "translation",
        RuntimeStage.Tts => "tts",
        RuntimeStage.Diarization => "diarization",
        RuntimeStage.Separation => "separation",
        RuntimeStage.SpeechEnhancement => "speech-enhancement",
        RuntimeStage.LipSync => "forced-alignment",
        RuntimeStage.TextRefinement => "text-refinement",
        RuntimeStage.OverlapRescue => "overlap-rescue",
        RuntimeStage.LipSynthesis => "lip-synthesis",
        _ => null,
    };

    private static void ValidateOptions(ControlledDubbingBenchmarkOptions options)
    {
        if (!File.Exists(options.FixturePath)) throw new FileNotFoundException("Fixture missing.", options.FixturePath);
        if (string.IsNullOrWhiteSpace(options.OutputDirectory)) throw new ArgumentException("Output directory required.");
        if (options.Mode is not ("fresh-process" or "warm-host" or "artifact-resume"))
            throw new ArgumentException("Mode must be fresh-process, warm-host, or artifact-resume.");
        if (options.Provider is not null && !Enum.TryParse<ExecutionProviderKind>(options.Provider, true, out _))
            throw new ArgumentException("Unknown provider.");
        if (!options.Mock && !options.DryRun && options.Provider is not null &&
            (ResolveStage(options.Stage) is not string stage || RuntimeStageFor(stage) is null))
            throw new ArgumentException("Provider pin requires a runtime-backed focused stage.");
        if (options.Model is not null)
        {
            // RuntimeStageFor mirrors the stages whose model preferences the pipeline
            // consumes (BuildModelPreferences); a focused stage outside that set (for
            // example audio-preparation, which runs a SpeechEnhancement model no
            // preference key can pin) would silently run its default model while the
            // report recorded options.Model as requested.
            if (ResolveStage(options.Stage) is not string modelStage ||
                RuntimeStageFor(modelStage) is null)
                throw new ArgumentException("Model selection requires a runtime-backed focused stage.");
            ValidateModelAlias(options.Model, modelStage);
        }
        if (options.ExpectedFixtureSha256 is not null &&
            (options.ExpectedFixtureSha256.Length != 64 ||
             !options.ExpectedFixtureSha256.All(Uri.IsHexDigit)))
            throw new ArgumentException("Expected fixture SHA-256 must be 64 hexadecimal characters.");
    }

    private static string? ResolveStage(string? requested)
    {
        if (requested is null) return null;
        string? match = DubbingPipelineStages.ExtendedStageOrder.SingleOrDefault(x =>
            x.Equals(requested, StringComparison.OrdinalIgnoreCase));
        if (match is not null) return match;

        return requested.ToLowerInvariant() switch
        {
            "audio-prep" => StageNames.AudioPreparation,
            "transcription" => StageNames.Asr,
            "alignment" => StageNames.LipSync,
            "dubbing" => StageNames.Tts,
            _ => throw new ArgumentException("Unknown stage.", nameof(requested)),
        };
    }

    private static IReadOnlyList<string> PrerequisitesFor(string stage)
    {
        if (stage.Equals(StageNames.LipSync, StringComparison.OrdinalIgnoreCase))
        {
            int ttsIndex = DubbingPipelineStages.DefaultStageOrder.ToList().FindIndex(x =>
                x.Equals(StageNames.Tts, StringComparison.OrdinalIgnoreCase));
            return DubbingPipelineStages.DefaultStageOrder.Take(ttsIndex + 1).ToArray();
        }

        int index = DubbingPipelineStages.DefaultStageOrder.ToList().FindIndex(x =>
            x.Equals(stage, StringComparison.OrdinalIgnoreCase));
        if (index < 0)
        {
            // Extended-only stages (OverlapRescue, TextRefinementAsr, LipSynthesis) have no
            // index in DefaultStageOrder; derive their prerequisites from the ExtendedStageOrder
            // prefix instead, filtered down to the default-order stages the runner can prepare.
            int extendedIndex = DubbingPipelineStages.ExtendedStageOrder.ToList().FindIndex(x =>
                x.Equals(stage, StringComparison.OrdinalIgnoreCase));
            if (extendedIndex <= 0) return [];
            return DubbingPipelineStages.ExtendedStageOrder
                .Take(extendedIndex)
                .Where(candidate => DubbingPipelineStages.DefaultStageOrder.Any(defaultStage =>
                    defaultStage.Equals(candidate, StringComparison.OrdinalIgnoreCase)))
                .ToArray();
        }
        if (index <= 0) return [];
        return DubbingPipelineStages.DefaultStageOrder.Take(index).ToArray();
    }

    private static void CopyDirectory(string sourceDir, string destinationDir)
    {
        Directory.CreateDirectory(destinationDir);
        if (!Directory.Exists(sourceDir))
        {
            return;
        }

        foreach (string file in Directory.GetFiles(sourceDir))
        {
            string destFile = Path.Join(destinationDir, Path.GetFileName(file));
            File.Copy(file, destFile, overwrite: true);
        }

        foreach (string subDir in Directory.GetDirectories(sourceDir))
        {
            string destSubDir = Path.Join(destinationDir, Path.GetFileName(subDir));
            CopyDirectory(subDir, destSubDir);
        }
    }

    private HeadlessDubbingHost CreateHost(ControlledDubbingBenchmarkOptions options)
    {
        Dictionary<string, ExecutionProviderKind>? pins = null;
        string? stage = ResolveStage(options.Stage);
        if (options.Provider is not null && stage is not null &&
            RuntimeStageFor(stage) is RuntimeStage runtimeStage)
        {
            pins = new Dictionary<string, ExecutionProviderKind>
            {
                [runtimeStage.ToString()] = Enum.Parse<ExecutionProviderKind>(options.Provider, true),
            };
        }

        Action<IServiceCollection>? configurator = _serviceConfigurator;
        if (options.Mock || options.DryRun)
        {
            var prev = configurator;
            configurator = services =>
            {
                prev?.Invoke(services);
                var existingDescriptor = services.FirstOrDefault(s => s.ServiceType == typeof(MockPipelineOptions));
                if (existingDescriptor?.ImplementationInstance is MockPipelineOptions existingOpts)
                {
                    existingOpts.DryRun = options.DryRun;
                    if (options.Provider is not null) existingOpts.DefaultProvider = options.Provider;
                    if (options.Model is not null) existingOpts.DefaultModel = options.Model;
                }
                else
                {
                    MockDubbingPipelineServices.ConfigureMockPipeline(services, mockOpts =>
                    {
                        mockOpts.DryRun = options.DryRun;
                        if (options.Provider is not null)
                        {
                            mockOpts.DefaultProvider = options.Provider;
                        }
                        if (options.Model is not null)
                        {
                            mockOpts.DefaultModel = options.Model;
                        }
                    });
                }
            };
        }

        return HeadlessDubbingHost.Create(new HeadlessTrackdubOptions
        {
            ModelDirectory = options.ModelDirectory,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = options.FfprobePath,
            HardwareOverrides = pins,
            RequirePreferredExecutionProviders = pins is not null,
            ServiceConfigurator = configurator,
        });
    }

    private static RuntimeStage? RuntimeStageFor(string stage) => stage switch
    {
        StageNames.Separation => RuntimeStage.Separation,
        StageNames.Vad => RuntimeStage.Vad,
        StageNames.Diarization => RuntimeStage.Diarization,
        StageNames.Asr => RuntimeStage.Asr,
        StageNames.OverlapRescue => RuntimeStage.OverlapRescue,
        StageNames.TextRefinementAsr => RuntimeStage.TextRefinement,
        StageNames.Translation => RuntimeStage.Translation,
        StageNames.Tts => RuntimeStage.Tts,
        StageNames.LipSync => RuntimeStage.LipSync,
        StageNames.LipSynthesis => RuntimeStage.LipSynthesis,
        _ => null,
    };

    private static void RequirePreparationSucceeded(
        DubbingRunResult preparation,
        string failureMessage,
        out string? reason,
        out BenchmarkEvidenceStatus status,
        out IReadOnlyList<BenchmarkEvidenceStage> stages)
    {
        if (preparation.OverallStatus != DubbingRunStatus.Succeeded ||
            preparation.StageOutcomes.Any(x => x.Status != StageStatus.Succeeded &&
                !(x.Status == StageStatus.Skipped &&
                    StageSkipReasonCodes.IsBenignSkip(x.ReasonCode))))
        {
            reason = failureMessage;
            status = BenchmarkEvidenceStatus.Skipped;
            stages = MapStages(preparation, [], null, null);
            throw new PreparationIncompleteException();
        }

        reason = null;
        status = BenchmarkEvidenceStatus.Skipped;
        stages = [];
    }

    private static Task<DubbingRunResult> ExecuteAsync(
        HeadlessDubbingHost host, string fixture, string project, ControlledDubbingBenchmarkOptions options,
        IReadOnlyList<string>? stages, bool forceRerun, CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        if (host.Services.GetService<IDubbingPipelineService>() is { } mockService)
        {
            return mockService.ExecuteAsync(new DubbingSessionOptions
            {
                SourceMediaPath = fixture,
                ProjectOutputDirectory = project,
                SourceLanguageCode = options.SourceLanguage,
                TargetLanguageCode = options.TargetLanguage,
                StageFilter = stages,
                ModelPreferences = options.Model is null || options.Stage is null ? null : new Dictionary<string, string> { [options.Stage] = options.Model },
                ForceRerun = forceRerun,
            }, progress, cancellationToken);
        }

        string? stage = ResolveStage(options.Stage);
        IReadOnlyDictionary<string, string>? models = options.Model is null || stage is null
            ? null : new Dictionary<string, string> { [stage] = options.Model };
        return host.CreateEngine().ExecuteAsync(new DubbingSessionOptions
        {
            SourceMediaPath = fixture,
            ProjectOutputDirectory = project,
            SourceLanguageCode = options.SourceLanguage,
            TargetLanguageCode = options.TargetLanguage,
            StageFilter = stages,
            ModelPreferences = models,
            ForceRerun = forceRerun,
        }, progress, cancellationToken);
    }

    private static async Task<RunArtifacts> ReadRunArtifactsAsync(
        HeadlessDubbingHost host, string project, ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        if (host.Services.GetService<IDubbingPipelineService>() is { } mockService)
        {
            return new RunArtifacts(
                mockService.GetStageRuns(project, options.Provider, options.Model),
                HasUsableTranscript: true,
                HasPlayableTake: true,
                MediaDurationSeconds: 1.0);
        }

        await using IDubbingSession session = host.SessionFactory.CreateSession(project,
            StudioSettings.Default with
            {
                DefaultSourceLanguage = options.SourceLanguage,
                DefaultTargetLanguage = options.TargetLanguage,
            });
        var state = await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        // Project.OpenAsync applies a UI-only provider downgrade to historical
        // stage rows when current hardware differs. Benchmark provenance must
        // use the persisted execution record, not that display projection.
        var rawStageStore = new SqliteProjectStageRunStore(new SqliteProjectDatabase(project));
        IReadOnlyList<StageRunRecord> rawStageRuns = await rawStageStore
            .ListByProjectAsync(state.ProjectState.Project.Id, cancellationToken).ConfigureAwait(false);
        bool playableTake = state.TtsTakes.Any(take =>
            take.Status == TtsTakeStatus.Completed && take.ArtifactId is Guid id &&
            state.ProjectState.Artifacts.Any(artifact =>
                artifact.Id == id && artifact.SizeBytes > 0 &&
                File.Exists(Path.Join(project, artifact.RelativePath))));
        return new RunArtifacts(
            rawStageRuns,
            state.TranscriptSegments.Any(segment => !string.IsNullOrWhiteSpace(segment.Text)),
            playableTake,
            state.ProjectState.MediaAsset?.DurationSeconds ?? 0);
    }

    private static IReadOnlyList<BenchmarkEvidenceStage> MapStages(
        DubbingRunResult result,
        IReadOnlyList<StageRunRecord> records,
        string? requestedModel,
        IReadOnlyDictionary<string, List<double>>? stageSamples = null,
        StageTimingCollector? stageClock = null) =>
        result.StageOutcomes.Select(outcome =>
        {
            StageRunRecord? record = records.LastOrDefault(x =>
                x.StageName.Equals(outcome.StageName, StringComparison.OrdinalIgnoreCase) &&
                x.StartedAtUtc >= result.StartTime.AddSeconds(-1));

            double? duration = stageSamples is not null &&
                stageSamples.TryGetValue(outcome.StageName, out var samples) &&
                samples.Count > 0
                    ? PercentileCalculator.Calculate(samples).P50Milliseconds
                    : stageClock?.GetMilliseconds(outcome.StageName);

            return new BenchmarkEvidenceStage
            {
                Name = outcome.StageName,
                Status = outcome.Status switch
                {
                    StageStatus.Succeeded => BenchmarkEvidenceStatus.Completed,
                    StageStatus.PartiallySucceeded => BenchmarkEvidenceStatus.PartiallyCompleted,
                    StageStatus.Skipped => BenchmarkEvidenceStatus.Skipped,
                    _ => BenchmarkEvidenceStatus.Failed,
                },
                Reason = outcome.ReasonCode,
                StageRunId = record?.Id,
                StartedAtUtc = outcome.StartTime,
                CompletedAtUtc = outcome.EndTime,
                DurationMilliseconds = duration,
                RequestedModel = requestedModel,
                ActualModel = record?.RuntimeInfo?.ModelAlias ?? record?.RuntimeInfo?.ModelId,
                RequestedProvider = record?.RuntimeInfo?.RequestedProvider,
                ActualProvider = record?.RuntimeInfo?.SelectedProvider,
            };
        }).ToArray();

    private sealed record RunArtifacts(
        IReadOnlyList<StageRunRecord> StageRuns,
        bool HasUsableTranscript,
        bool HasPlayableTake,
        double MediaDurationSeconds = 0);

    private sealed class EnvironmentOverride(string name, string value) : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(name);
        public void Dispose() => Environment.SetEnvironmentVariable(name, _previous);
        public void Apply() => Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class PreparationIncompleteException : Exception;
}
