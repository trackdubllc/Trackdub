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
using Trackdub.Infrastructure.Diagnostics;
using Trackdub.Infrastructure.Settings;
using Trackdub.Benchmarks.Scenarios;
using Microsoft.Extensions.DependencyInjection;

namespace Trackdub.Benchmarks;

/// <summary>Runs an isolated fixture through a real pipeline, preserving terminal evidence.</summary>
public sealed class ControlledDubbingBenchmarkRunner : IDisposable
{
    private readonly IBenchmarkEvidenceRepository _history;
    private readonly Action<IServiceCollection>? _serviceConfigurator;
    private readonly IWorkingSetPeakMonitorFactory _monitorFactory = new WorkingSetPeakMonitorFactory();
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

    internal ControlledDubbingBenchmarkRunner(
        IBenchmarkEvidenceRepository? history,
        Action<IServiceCollection>? serviceConfigurator,
        IWorkingSetPeakMonitorFactory monitorFactory) : this(history, serviceConfigurator)
    {
        _monitorFactory = monitorFactory ?? throw new ArgumentNullException(nameof(monitorFactory));
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
        var context = new BenchmarkRunContext(options, stage, Guid.NewGuid(), _monitorFactory);
        try
        {
            await PrepareHostAsync(context, options, cancellationToken).ConfigureAwait(false);
            await PreparePrerequisitesAsync(context, options, cancellationToken).ConfigureAwait(false);
            await RunPreparationPhasesAsync(context, options, cancellationToken).ConfigureAwait(false);
            await MeasureRunsAsync(context, options, cancellationToken).ConfigureAwait(false);
            await RecordMeasuredOutcomeAsync(context, options, cancellationToken).ConfigureAwait(false);
        }
        catch (PreparationIncompleteException)
        {
            // The preparation result and reason have already been captured.
        }
        catch (OperationCanceledException)
        {
            context.Status = BenchmarkEvidenceStatus.Canceled;
            context.Reason = "Canceled.";
        }
        catch (Exception ex)
        {
            context.Status = BenchmarkEvidenceStatus.Failed;
            context.Reason = $"{ex.GetType().Name}: {ex.Message}";
        }
        finally
        {
            if (context.OwnsHost && context.Host is not null)
            {
                long disposeStart = Stopwatch.GetTimestamp();
                context.Host.Dispose();
                context.Timings["disposal"] = Stopwatch.GetElapsedTime(disposeStart).TotalMilliseconds;
            }
            context.CacheScope?.Dispose();
        }
        return await CreateReportAsync(context, options, cancellationToken).ConfigureAwait(false);
    }

    private async Task PrepareHostAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Directory.CreateDirectory(context.ProjectRoot);
        long preparationStart = Stopwatch.GetTimestamp();
        context.FixtureHash = await CopyFixtureAsync(options.FixturePath, context.FixtureCopy, cancellationToken)
            .ConfigureAwait(false);
        context.Timings["fixturePreparation"] = Stopwatch.GetElapsedTime(preparationStart).TotalMilliseconds;
        if (options.ExpectedFixtureSha256 is not null &&
            !context.FixtureHash.Equals(options.ExpectedFixtureSha256, StringComparison.OrdinalIgnoreCase))
        {
            context.Status = BenchmarkEvidenceStatus.Failed;
            context.Reason = "Fixture checksum differs from the expected SHA-256.";
            throw new PreparationIncompleteException();
        }

        if (options.Mode == "fresh-process" && !options.ReuseEngineCache)
        {
            string cache = Path.Join(context.ProjectRoot, "engine-cache");
            Directory.CreateDirectory(cache);
            context.CacheScope = new EnvironmentOverride(TrackdubStoragePathResolver.EngineCacheRootEnvironmentVariable, cache);
            ((EnvironmentOverride)context.CacheScope).Apply();
        }

        long hostStart = Stopwatch.GetTimestamp();
        context.OwnsHost = options.Mode != "warm-host";
        context.Host = context.OwnsHost ? CreateHost(options) : AcquireWarmHost(options);
        // Resolve the resource probe during host setup so its one-time initialization is charged
        // to hostCreation rather than to the first measured iteration. The first Windows
        // performance-counter call measured ~1300 ms on a cold host, and a full-pipeline run has
        // no untimed prerequisite phase in front of iteration 1 to absorb it.
        _ = context.Host.Services.GetRequiredService<IResourceTelemetryCollector>();
        // Headless storage overrides may set this variable while building the host.
        // Apply the per-sample engine-cache root again before any session is created.
        if (context.CacheScope is EnvironmentOverride engineCache)
            engineCache.Apply();
        context.Timings["hostCreation"] = Stopwatch.GetElapsedTime(hostStart).TotalMilliseconds;

        // Sanitize the resource bounds against this host's real adapter capacity before anything is
        // measured. A free-VRAM floor above the video memory the host can address is physically
        // impossible: rejecting it here keeps the run from burning a full benchmark only to report
        // a bound the hardware could never have met. Skip the query when no floor is configured
        // to avoid pre-warming device discovery and altering cold-run measurements.
        if (options.ResourceTelemetryBounds.MinAvailableVramMb > 0)
        {
            long totalVideoMemoryMb = await ResourceBoundsPreflight
                .QueryTotalVideoMemoryMbAsync(context.Host.Services, cancellationToken)
                .ConfigureAwait(false);
            if (ResourceBoundsPreflight.DescribeImpossibleBound(
                    options.ResourceTelemetryBounds, totalVideoMemoryMb) is string impossibleBound)
            {
                context.Status = BenchmarkEvidenceStatus.Failed;
                context.Reason = impossibleBound;
                throw new PreparationIncompleteException();
            }
        }
    }

    private async Task PreparePrerequisitesAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        string? stage = context.Stage;
        if (stage is null)
        {
            return;
        }

        IReadOnlyList<string> prerequisites = PrerequisitesFor(stage);
        if (prerequisites.Count == 0)
        {
            return;
        }

        context.HasPrerequisites = true;
        Directory.CreateDirectory(Path.GetDirectoryName(context.BaselineProjectPath)!);
        long prerequisiteStart = Stopwatch.GetTimestamp();
        DubbingRunResult preparation = await ExecuteWithTelemetryAsync(
            context, options, context.BaselineProjectPath, prerequisites, true,
            "prerequisites", 0, stageClock: null, cancellationToken).ConfigureAwait(false);
        context.Timings["prerequisites"] = Stopwatch.GetElapsedTime(prerequisiteStart).TotalMilliseconds;
        RequirePreparationSucceeded(
            preparation, "Prerequisite preparation did not complete successfully.", context);

        // A stage already run by its prerequisites is timed as an in-place re-run
        // (for ASR: re-transcribing existing segments), not as the stage itself.
        // No prerequisite regenerates a later stage today, but guard against one
        // sneaking the timed stage in (e.g. an opt-in stage running during import).
        RunArtifacts prepared = await ReadRunArtifactsAsync(context.Host!, context.BaselineProjectPath, options, cancellationToken)
            .ConfigureAwait(false);
        if (prepared.StageRuns.Any(run => run.StageName.Equals(stage, StringComparison.OrdinalIgnoreCase)))
        {
            context.Reason = $"Prerequisites already ran '{stage}'; the timed run would measure a re-run.";
            context.Status = BenchmarkEvidenceStatus.Skipped;
            throw new PreparationIncompleteException();
        }
    }

    private async Task RunPreparationPhasesAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<string>? filter = context.Stage is null ? null : [context.Stage];
        if (options.Mode == "warm-host")
        {
            string warmupProjectPath = Path.Join(context.ProjectRoot, "warmup", "project.trackdub");
            SeedProjectDirectory(context.HasPrerequisites ? context.BaselineProjectPath : null, warmupProjectPath);

            long warmupStart = Stopwatch.GetTimestamp();
            DubbingRunResult warmup = await ExecuteWithTelemetryAsync(
                context, options, warmupProjectPath, filter, true,
                "warmup", 0, stageClock: null, cancellationToken).ConfigureAwait(false);
            context.Timings["warmup"] = Stopwatch.GetElapsedTime(warmupStart).TotalMilliseconds;
            RequirePreparationSucceeded(
                warmup, "Warm-host preparation did not complete successfully.", context);
        }

        if (options.Mode == "artifact-resume")
        {
            context.PrimingProjectPath = Path.Join(context.ProjectRoot, "priming", "project.trackdub");
            SeedProjectDirectory(context.HasPrerequisites ? context.BaselineProjectPath : null, context.PrimingProjectPath);

            DubbingRunResult priming = await ExecuteWithTelemetryAsync(
                context, options, context.PrimingProjectPath, filter, true,
                "priming", 0, stageClock: null, cancellationToken).ConfigureAwait(false);
            if (priming.OverallStatus != DubbingRunStatus.Succeeded)
            {
                context.Reason = "Artifact-resume preparation did not complete successfully.";
                context.Status = BenchmarkEvidenceStatus.Skipped;
                context.Stages = MapStages(priming, [], null, null, null);
                throw new PreparationIncompleteException();
            }
        }
    }

    private async Task MeasureRunsAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        int runCount = Math.Max(1, options.RunCount);
        var stageSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var stageMemorySamples = new Dictionary<string, List<ResourceTelemetryDelta>>(StringComparer.OrdinalIgnoreCase);
        var pipelineSamples = new List<double>(runCount);
        var phaseSamples = new Dictionary<string, List<double>>(StringComparer.OrdinalIgnoreCase);
        var firstOutputSamples = new Dictionary<string, List<double>>(StringComparer.Ordinal);
        DubbingRunResult lastResult = null!;
        StageTimingCollector lastClock = null!;
        string lastProjectPath = null!;
        IReadOnlyList<string>? filter = context.Stage is null ? null : [context.Stage];

        for (int runIndex = 1; runIndex <= runCount; runIndex++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string iterProjectPath = Path.Join(context.ProjectRoot, $"run_{runIndex}", "project.trackdub");

            // Artifact-resume seeds from the primed project so the measured run actually
            // resumes/skips the already-completed stages instead of doing a full cold run.
            SeedProjectDirectory(
                options.Mode == "artifact-resume" ? context.PrimingProjectPath
                    : context.HasPrerequisites ? context.BaselineProjectPath : null,
                iterProjectPath);

            long runStart = Stopwatch.GetTimestamp();
            var stageClock = new StageTimingCollector(runStart);
            var phases = new BenchmarkPhaseCapture();
            DubbingRunResult iterResult;
            using (BenchmarkPhaseCapture.Activate(phases))
            {
                iterResult = await ExecuteWithTelemetryAsync(
                    context, options, iterProjectPath, filter, options.Mode != "artifact-resume",
                    "measured", runIndex, stageClock, cancellationToken).ConfigureAwait(false);
            }

            double pipeDuration = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
            pipelineSamples.Add(pipeDuration);

            foreach ((string name, double? duration) in phases.SnapshotMilliseconds())
            {
                if (duration is double phaseMs)
                    AddSample(phaseSamples, name, phaseMs);
            }

            foreach ((string counter, long count) in phases.SnapshotCounters())
                context.CounterTotals[counter] = context.CounterTotals.GetValueOrDefault(counter) + count;
            foreach ((string gauge, long value) in phases.SnapshotMaxima())
                context.ObservedMaxima[gauge] = Math.Max(context.ObservedMaxima.GetValueOrDefault(gauge), value);

            foreach (var outcome in iterResult.StageOutcomes)
            {
                if (stageClock.GetMilliseconds(outcome.StageName) is double stageMs)
                    AddSample(stageSamples, outcome.StageName, stageMs);
                if (stageClock.GetMemoryDelta(outcome.StageName) is ResourceTelemetryDelta memDelta)
                    AddSample(stageMemorySamples, outcome.StageName, memDelta);
            }

            if (stageClock.GetFirstOutputMilliseconds(
                    PipelineOutputKind.TranscriptSegmentAvailable) is double firstUsableTranscript)
                AddSample(firstOutputSamples, "firstUsableTranscript", firstUsableTranscript);
            if (stageClock.GetFirstOutputMilliseconds(
                    PipelineOutputKind.TranscriptSegmentPersisted) is double firstPersistedTranscript)
                AddSample(firstOutputSamples, "firstPersistedTranscript", firstPersistedTranscript);
            if (stageClock.GetFirstOutputMilliseconds(
                    PipelineOutputKind.PlayableAudioPersisted) is double firstPlayableAudio)
                AddSample(firstOutputSamples, "firstPlayableAudio", firstPlayableAudio);

            lastResult = iterResult;
            lastClock = stageClock;
            lastProjectPath = iterProjectPath;

            if (iterResult.OverallStatus != DubbingRunStatus.Succeeded)
            {
                break;
            }
        }

        context.MeasuredRuns = new MeasuredRuns(
            stageSamples, stageMemorySamples, pipelineSamples, phaseSamples,
            firstOutputSamples, lastResult, lastClock, lastProjectPath);
    }

    private async Task RecordMeasuredOutcomeAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        MeasuredRuns runs = context.MeasuredRuns;
        RunArtifacts artifacts = await ReadRunArtifactsAsync(context.Host!, runs.LastProjectPath, options, cancellationToken)
            .ConfigureAwait(false);
        double mediaDuration = artifacts.MediaDurationSeconds;

        RecordStageTimings(context.Timings, runs.StageSamples, mediaDuration, context.Stage);
        context.StageGarbageCollection = BuildStageGarbageCollection(
            runs.StageSamples.Keys, runs.StageMemorySamples, runs.LastClock);

        if (runs.PipelineSamples.Count > 0)
        {
            RecordLatencyStatistics(context.Timings, "pipeline", PercentileCalculator.Calculate(
                runs.PipelineSamples,
                totalUnits: mediaDuration,
                totalDurationSeconds: runs.PipelineSamples.Sum() / 1000.0));
        }

        foreach ((string name, List<double> pSamples) in runs.PhaseSamples)
        {
            context.Timings[name] = PercentileCalculator.Calculate(pSamples).P50Milliseconds;
        }

        context.Timings["import"] = context.Timings.GetValueOrDefault("phase:import");
        context.Timings["preflight"] = context.Timings.GetValueOrDefault("phase:preflight");
        context.Timings["export"] = context.Timings.GetValueOrDefault("stage:Export:p50")
            ?? runs.LastClock?.GetMilliseconds("Export");

        context.RunId = runs.LastResult.RunId;
        context.Stages = MapStages(runs.LastResult, artifacts.StageRuns, options.Model, runs.StageSamples, runs.LastClock);

        // TTFT comes only from structured output events — stage completion is not a
        // substitute for the first usable artifact. Artifact presence gates whether a
        // recorded event is accepted into the report; it never creates a timing alone.
        if (artifacts.HasUsableTranscript)
        {
            context.Timings["firstUsableTranscript"] = runs.FirstOutputSamples.TryGetValue(
                "firstUsableTranscript", out List<double>? firstUsableSamples)
                    ? PercentileCalculator.Calculate(firstUsableSamples).P50Milliseconds
                    : null;
            context.Timings["firstPersistedTranscript"] = runs.FirstOutputSamples.TryGetValue(
                "firstPersistedTranscript", out List<double>? firstPersistedSamples)
                    ? PercentileCalculator.Calculate(firstPersistedSamples).P50Milliseconds
                    : null;
        }

        if (artifacts.HasPlayableTake)
        {
            context.Timings["firstPlayableAudio"] = runs.FirstOutputSamples.TryGetValue(
                "firstPlayableAudio", out List<double>? firstPlayableSamples)
                    ? PercentileCalculator.Calculate(firstPlayableSamples).P50Milliseconds
                    : null;
        }

        BenchmarkEvidenceStage? requestedStage = FindRequestedStage(context.Stages, context.Stage);
        context.ActualModel = requestedStage?.ActualModel;
        context.ActualProvider = requestedStage?.ActualProvider;
        (context.Status, context.Reason) = ResolveRunOutcome(
            context.Stage, requestedStage, runs.LastResult, context.Stages,
            options.Provider, context.ActualProvider, context.Reason);
    }

    private async Task<BenchmarkEvidenceReport> CreateReportAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        CancellationToken cancellationToken)
    {
        context.Clock.Stop();
        ResourceTelemetrySnapshot? processTelemetryEnd = ResourceTelemetry.TryCaptureProcess();
        ResourceTelemetryDelta? processDelta = context.ProcessTelemetryStart is not null && processTelemetryEnd is not null
            ? ResourceTelemetry.CalculateDelta(context.ProcessTelemetryStart, processTelemetryEnd) : null;

        long? sampledProcessPeak = context.ProcessWorkingSetPeak?.Stop();
        BenchmarkProcessMemoryTelemetry? processMemory = BuildProcessMemory(
            context.ProcessTelemetryStart, processTelemetryEnd, processDelta, sampledProcessPeak);

        EnsureMeasuredTelemetry(context.ResourceTelemetry, context.Stage, context.Reason);
        string[] resourceFailures = CollectResourceFailures(context.ResourceTelemetry);
        if (resourceFailures.Length > 0)
        {
            if (context.Status != BenchmarkEvidenceStatus.Canceled) context.Status = BenchmarkEvidenceStatus.Failed;
            context.Reason = string.Join("; ", new[] { context.Reason, "Resource validation failed: " + string.Join("; ", resourceFailures) }
                .Where(text => !string.IsNullOrWhiteSpace(text)));
        }
        ResourceTelemetryStatus resourceStatus = ResolveResourceStatus(context.ResourceTelemetry, resourceFailures.Length > 0);

        context.Timings["total"] = context.Clock.Elapsed.TotalMilliseconds;
        var counters = new Dictionary<string, long?>(StringComparer.Ordinal);
        foreach ((string name, long value) in context.CounterTotals)
            counters[name] = value;
        foreach ((string name, long value) in context.ObservedMaxima)
        {
            // A counter/max key collision is not expected by instrumentation
            // convention; if one occurs keep the larger value rather than
            // silently lowering an existing counter.
            if (!counters.TryGetValue(name, out long? existing) || value > existing)
                counters[name] = value;
        }
        var report = new BenchmarkEvidenceReport
        {
            RunId = context.RunId,
            Kind = BenchmarkEvidenceKind.Benchmark,
            Scenario = context.Stage ?? "full-pipeline",
            RunMode = DescribeRunMode(options),
            Status = context.Status,
            Reason = context.Reason,
            StartedAtUtc = context.StartedAt,
            CompletedAtUtc = DateTimeOffset.UtcNow,
            FixtureSha256 = context.FixtureHash,
            RequestedModel = options.Model,
            ActualModel = context.ActualModel,
            RequestedProvider = options.Provider,
            ActualProvider = context.ActualProvider,
            Configuration = BuildConfiguration(options, context.Stage, context.Stages, context.ProcessWorkingSetPeak),
            RuntimeVersions = CaptureRuntimeVersions(),
            TimingsMilliseconds = context.Timings,
            ProcessMemory = processMemory,
            StageGarbageCollection = context.StageGarbageCollection,
            Counters = counters,
            Stages = context.Stages,
            ResourceTelemetryBounds = options.ResourceTelemetryBounds,
            ResourceValidationStatus = resourceStatus,
            ResourceTelemetry = context.ResourceTelemetry.ToArray(),
            ResourceDistribution = ResourceTelemetryAggregator.Aggregate(context.ResourceTelemetry),
        };
        using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        CancellationToken saveToken = context.Status == BenchmarkEvidenceStatus.Canceled
            ? saveTimeout.Token
            : cancellationToken;
        await _history.SaveAsync(report, saveToken).ConfigureAwait(false);
        return report;
    }

    private async Task<DubbingRunResult> ExecuteWithTelemetryAsync(
        BenchmarkRunContext context,
        ControlledDubbingBenchmarkOptions options,
        string project, IReadOnlyList<string>? filter, bool forceRerun,
        string phase, int iteration, StageTimingCollector? stageClock,
        CancellationToken cancellationToken)
    {
        var capture = new StageResourceTelemetryCapture(
            context.Host!.Services.GetRequiredService<IResourceTelemetryCollector>(),
            context.Host.Services.GetRequiredService<IResourceTelemetryValidator>(),
            options.ResourceTelemetryBounds, phase, iteration, stageClock,
            context.Host.Services.GetRequiredService<IWorkingSetSampler>(),
            null, _monitorFactory,
            gpuMemoryReader: context.Host.Services.GetRequiredService<IProcessGpuMemoryReader>());
        try
        {
            DubbingRunResult result = await ExecuteAsync(context.Host, context.FixtureCopy, project, options,
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
            context.ResourceTelemetry.AddRange(capture.Snapshot());
        }
    }

    private static Dictionary<string, double?> CreateTimings() => new(StringComparer.Ordinal)
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
        ["firstPersistedTranscript"] = null,
        ["firstPlayableAudio"] = null,
    };

    /// <summary>
    /// Builds the typed run-level process memory envelope from the run's endpoint snapshots, the
    /// process delta, and the continuously-sampled peak.
    /// </summary>
    private static BenchmarkProcessMemoryTelemetry BuildProcessMemory(
        ResourceTelemetrySnapshot? processTelemetryStart,
        ResourceTelemetrySnapshot? processTelemetryEnd,
        ResourceTelemetryDelta? processDelta,
        long? sampledProcessPeak) => new()
        {
            WorkingSetStartBytes = processTelemetryStart?.WorkingSetBytes,
            WorkingSetEndBytes = processTelemetryEnd?.WorkingSetBytes,
            PeakWorkingSetBytes = ResolveProcessPeak(sampledProcessPeak,
                processTelemetryStart?.WorkingSetBytes, processTelemetryEnd?.WorkingSetBytes),
            ManagedAllocatedBytes = processDelta?.ManagedAllocatedBytes,
            Gen0Collections = processDelta?.Gen0Collections,
            Gen1Collections = processDelta?.Gen1Collections,
            Gen2Collections = processDelta?.Gen2Collections,
        };

    internal static long? ResolveProcessPeak(long? sampled, long? start, long? end) =>
        sampled ?? (start.HasValue && end.HasValue ? Math.Max(start.Value, end.Value) : start ?? end);

    private static async Task<string> CopyFixtureAsync(
        string fixturePath, string fixtureCopy, CancellationToken cancellationToken)
    {
        await using (FileStream source = File.OpenRead(fixturePath))
        await using (FileStream destination = File.Create(fixtureCopy))
        {
            await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
        }
        await using FileStream hashStream = File.OpenRead(fixtureCopy);
        return Convert.ToHexString(await SHA256.HashDataAsync(hashStream, cancellationToken)
            .ConfigureAwait(false)).ToLowerInvariant();
    }

    private HeadlessDubbingHost AcquireWarmHost(ControlledDubbingBenchmarkOptions options)
    {
        string key = string.Join("|", options.ModelDirectory, options.Provider,
            options.Stage, options.FfmpegPath, options.FfprobePath);
        if (_warmHostKey != key)
        {
            _warmHost?.Dispose();
            _warmHost = null;
            _warmHostKey = key;
        }
        return _warmHost ??= CreateHost(options);
    }

    private static void SeedProjectDirectory(string? seedProjectPath, string projectPath)
    {
        if (seedProjectPath is null)
            Directory.CreateDirectory(projectPath);
        else
            CopyDirectory(seedProjectPath, projectPath);
    }

    private static void AddSample<T>(Dictionary<string, List<T>> samples, string key, T value)
    {
        if (!samples.TryGetValue(key, out List<T>? list))
        {
            list = [];
            samples[key] = list;
        }

        list.Add(value);
    }

    private static void RecordLatencyStatistics(
        Dictionary<string, double?> timings, string key, LatencyStatistics stats)
    {
        timings[$"{key}:min"] = stats.MinMilliseconds;
        timings[$"{key}:max"] = stats.MaxMilliseconds;
        timings[$"{key}:mean"] = stats.MeanMilliseconds;
        timings[$"{key}:p50"] = stats.P50Milliseconds;
        timings[$"{key}:p90"] = stats.P90Milliseconds;
        timings[$"{key}:p99"] = stats.P99Milliseconds;
        timings[$"{key}:throughput"] = stats.ThroughputUnitsPerSecond;
        timings[$"{key}:sampleCount"] = (double)stats.SampleCount;
        timings[key] = stats.P50Milliseconds;
    }

    private static void RecordStageTimings(
        Dictionary<string, double?> timings,
        Dictionary<string, List<double>> stageSamples,
        double mediaDuration,
        string? requestedStage)
    {
        foreach ((string stageName, List<double> samples) in stageSamples)
        {
            LatencyStatistics stageStats = PercentileCalculator.Calculate(
                samples,
                totalUnits: mediaDuration,
                totalDurationSeconds: samples.Sum() / 1000.0);
            RecordLatencyStatistics(timings, $"stage:{stageName}", stageStats);

            string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
            if (!canonical.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                RecordLatencyStatistics(timings, $"stage:{canonical}", stageStats);
        }

        if (requestedStage is not null && !timings.ContainsKey($"stage:{requestedStage}:p50"))
            AliasRequestedStageTimings(timings, requestedStage);
    }

    private static void AliasRequestedStageTimings(Dictionary<string, double?> timings, string stage)
    {
        string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stage);
        if (!timings.TryGetValue($"stage:{canonical}:p50", out double? canP50))
            return;

        timings[$"stage:{stage}:p50"] = canP50;
        foreach (string metric in (string[])["min", "max", "mean", "p90", "p99", "throughput", "sampleCount"])
            timings[$"stage:{stage}:{metric}"] = timings.GetValueOrDefault($"stage:{canonical}:{metric}");
        timings[$"stage:{stage}"] = canP50;
    }

    /// <summary>
    /// Builds the typed per-stage GC deltas from the per-run memory samples, falling back to the
    /// stage clock's delta when a stage produced no samples. Working-set peaks and managed
    /// allocation for the same stages are already carried by the typed per-stage resource checks,
    /// so only the GC deltas are materialized here.
    /// </summary>
    private static List<BenchmarkStageGarbageCollectionTelemetry> BuildStageGarbageCollection(
        IEnumerable<string> measuredStages,
        Dictionary<string, List<ResourceTelemetryDelta>> stageMemorySamples,
        StageTimingCollector? lastClock)
    {
        var collections = new List<BenchmarkStageGarbageCollectionTelemetry>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        void Add(string stageKey, ResourceTelemetryDelta delta)
        {
            if (!seen.Add(stageKey))
                return;
            collections.Add(new BenchmarkStageGarbageCollectionTelemetry
            {
                Stage = stageKey,
                Gen0Collections = delta.Gen0Collections,
                Gen1Collections = delta.Gen1Collections,
                Gen2Collections = delta.Gen2Collections,
            });
        }

        foreach (string stageName in measuredStages)
        {
            ResourceTelemetryDelta? summary =
                stageMemorySamples.TryGetValue(stageName, out List<ResourceTelemetryDelta>? samples) && samples.Count > 0
                    ? MedianMemoryDelta(samples)
                    : lastClock?.GetMemoryDelta(stageName);
            if (summary is null)
                continue;

            Add(stageName, summary);
            string canonical = MockDubbingPipelineServices.CanonicalBenchmarkStage(stageName);
            if (!canonical.Equals(stageName, StringComparison.OrdinalIgnoreCase))
                Add(canonical, summary);
        }

        if (lastClock is not null)
        {
            foreach ((string stageKey, ResourceTelemetryDelta delta) in lastClock.GetAllMemoryDeltas())
                Add(stageKey, delta);
        }

        return collections;
    }

    // Only the allocation and GC fields are meaningful; working-set values come from sampling.
    private static ResourceTelemetryDelta MedianMemoryDelta(List<ResourceTelemetryDelta> samples) => new(
        WorkingSetDeltaBytes: 0,
        PeakWorkingSetBytes: 0,
        ManagedAllocatedBytes: (long)Math.Round(Median(samples.Select(s => (double)s.ManagedAllocatedBytes))),
        Gen0Collections: (int)Math.Round(Median(samples.Select(s => (double)s.Gen0Collections))),
        Gen1Collections: (int)Math.Round(Median(samples.Select(s => (double)s.Gen1Collections))),
        Gen2Collections: (int)Math.Round(Median(samples.Select(s => (double)s.Gen2Collections))));

    private static double Median(IEnumerable<double> values) =>
        PercentileCalculator.CalculatePercentile(values.OrderBy(x => x).ToArray(), 0.5);

    private static BenchmarkEvidenceStage? FindRequestedStage(
        IReadOnlyList<BenchmarkEvidenceStage> stages, string? stage)
    {
        if (stage is null)
            return null;

        string canonicalStage = MockDubbingPipelineServices.CanonicalBenchmarkStage(stage);
        return stages.LastOrDefault(x =>
            x.Name.Equals(stage, StringComparison.OrdinalIgnoreCase) ||
            x.Name.Equals(canonicalStage, StringComparison.OrdinalIgnoreCase) ||
            MockDubbingPipelineServices.CanonicalBenchmarkStage(x.Name).Equals(canonicalStage, StringComparison.OrdinalIgnoreCase));
    }

    private static (BenchmarkEvidenceStatus Status, string? Reason) ResolveRunOutcome(
        string? stage,
        BenchmarkEvidenceStage? requestedStage,
        DubbingRunResult lastResult,
        IReadOnlyList<BenchmarkEvidenceStage> stages,
        string? requestedProvider,
        string? actualProvider,
        string? reason)
    {
        if (stage is not null && requestedStage?.Status != BenchmarkEvidenceStatus.Completed)
        {
            return (requestedStage?.Status ?? BenchmarkEvidenceStatus.Skipped,
                requestedStage?.Reason
                    ?? (lastResult.PreFlightFailures is { Count: > 0 }
                        ? "Preflight failed: " + string.Join("; ", lastResult.PreFlightFailures)
                        : "Requested stage produced no successful outcome."));
        }

        if (lastResult.OverallStatus != DubbingRunStatus.Succeeded)
        {
            BenchmarkEvidenceStatus status = lastResult.OverallStatus == DubbingRunStatus.PartialSuccess
                ? BenchmarkEvidenceStatus.PartiallyCompleted
                : BenchmarkEvidenceStatus.Failed;
            string failure = lastResult.PreFlightFailures is { Count: > 0 }
                ? "Preflight failed."
                : string.Join("; ", stages
                    .Where(measured => measured.Status != BenchmarkEvidenceStatus.Completed)
                    .Select(measured => $"{measured.Name}:{measured.Reason ?? measured.Status.ToString()}"));
            return (status, string.IsNullOrWhiteSpace(failure) ? "Pipeline did not complete successfully." : failure);
        }

        if (requestedProvider is not null &&
            (actualProvider is null || !BenchmarkComparison.ProviderMatches(requestedProvider, actualProvider)))
        {
            return (BenchmarkEvidenceStatus.PartiallyCompleted,
                "Actual provider was unavailable or differed from requested provider.");
        }

        return (BenchmarkEvidenceStatus.Completed, reason);
    }

    private static void EnsureMeasuredTelemetry(
        List<BenchmarkStageResourceTelemetry> resourceTelemetry, string? stage, string? reason)
    {
        if (resourceTelemetry.Any(sample => sample.Phase == "measured"))
            return;

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

    private static string[] CollectResourceFailures(IReadOnlyList<BenchmarkStageResourceTelemetry> resourceTelemetry) =>
        resourceTelemetry.SelectMany(sample => sample.Validation.Checks
            .Where(check => check.Status == ResourceTelemetryStatus.Failed)
            .Select(check => $"{sample.Stage} ({sample.Phase}, iteration {sample.Iteration}, attempt {sample.Attempt}): {check.Metric}: {check.Reason}"))
            .ToArray();

    private static ResourceTelemetryStatus ResolveResourceStatus(
        IReadOnlyList<BenchmarkStageResourceTelemetry> resourceTelemetry, bool hasFailures)
    {
        if (hasFailures)
            return ResourceTelemetryStatus.Failed;
        if (resourceTelemetry.Any(sample => sample.Validation.Status == ResourceTelemetryStatus.Unavailable))
            return ResourceTelemetryStatus.Unavailable;
        return resourceTelemetry.All(sample => sample.Validation.Status == ResourceTelemetryStatus.Skipped)
            ? ResourceTelemetryStatus.Skipped
            : ResourceTelemetryStatus.Passed;
    }

    private static string DescribeRunMode(ControlledDubbingBenchmarkOptions options) =>
        options.Mode == "fresh-process"
            ? options.ReuseEngineCache ? "fresh-process-compatible-cache" : "fresh-process-isolated-engine-cache"
            : options.Mode;

    private static Dictionary<string, string> BuildConfiguration(
        ControlledDubbingBenchmarkOptions options,
        string? stage,
        IReadOnlyList<BenchmarkEvidenceStage> stages,
        IWorkingSetPeakMonitor? processWorkingSetPeak)
    {
        var configuration = new Dictionary<string, string>
        {
            ["targetLanguage"] = options.TargetLanguage,
            ["sourceLanguage"] = options.SourceLanguage ?? "auto",
            ["stage"] = stage ?? "all",
            ["hardware"] = BenchmarkHardwareInfo.Capture(),
            ["resourceScope"] = "Process-wide; includes concurrent work; excludes child processes.",
            ["cpuNormalization"] = "100 * delta CPU milliseconds / (monotonic elapsed milliseconds * processor count)",
            ["memorySampling"] = "Process working set sampled every 25 ms across each stage and the benchmark run; excursions shorter than the cadence may be missed. A sampler failure is reported as unavailable.",
            ["workingSetPeakSampling"] = SamplingCaveat(processWorkingSetPeak),
            ["vramScope"] = "Adapter-wide free VRAM (budget minus current usage), not this process's allocation; moves with other processes on the same GPU.",
        };
        foreach (BenchmarkEvidenceStage measuredStage in stages)
        {
            if (measuredStage.ActualModel is not null)
                configuration[$"stage:{measuredStage.Name}:model"] = measuredStage.ActualModel;
            if (measuredStage.ActualProvider is not null)
                configuration[$"stage:{measuredStage.Name}:provider"] = measuredStage.ActualProvider;
        }
        return configuration;
    }

    private static string SamplingCaveat(IWorkingSetPeakMonitor? processWorkingSetPeak)
    {
        // The run-level monitor usually stops before this is read, so a dilation warning
        // observed across setup, pipeline, and export is appended instead of staying silent.
        string caveat = processWorkingSetPeak?.UnavailableReason
            ?? "Sampled at a 25 ms cadence; excursions shorter than the cadence may be missed.";
        if (processWorkingSetPeak?.SamplingWarning is string warning)
        {
            caveat += " " + warning;
        }

        return caveat;
    }

    private static Dictionary<string, string> CaptureRuntimeVersions() =>
        BenchmarkRuntimeVersions.Capture();

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
        BenchmarkRunContext context)
    {
        if (preparation.OverallStatus != DubbingRunStatus.Succeeded ||
            preparation.StageOutcomes.Any(x => x.Status != StageStatus.Succeeded &&
                !(x.Status == StageStatus.Skipped &&
                    StageSkipReasonCodes.IsBenignSkip(x.ReasonCode))))
        {
            context.Reason = failureMessage;
            context.Status = BenchmarkEvidenceStatus.Skipped;
            context.Stages = MapStages(preparation, [], null, null);
            throw new PreparationIncompleteException();
        }

        context.Reason = null;
        context.Status = BenchmarkEvidenceStatus.Skipped;
        context.Stages = [];
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

    // Mutable state shared across the async run phases. Async phases cannot use out
    // parameters, and preparation failures must record status/reason before throwing, so
    // the phases mutate this context instead of returning every accumulated value.
    private sealed class BenchmarkRunContext
    {
        public BenchmarkRunContext(ControlledDubbingBenchmarkOptions options, string? stage, Guid reportId, IWorkingSetPeakMonitorFactory monitorFactory)
        {
            Stage = stage;
            ReportId = reportId;
            StartedAt = DateTimeOffset.UtcNow;
            Clock = Stopwatch.StartNew();
            Timings = CreateTimings();
            ResourceTelemetry = new List<BenchmarkStageResourceTelemetry>();
            StageGarbageCollection = [];
            ProcessTelemetryStart = Metrics.ResourceTelemetry.TryCaptureProcess();
            ProcessWorkingSetPeak = monitorFactory.Create(
                new ProcessWorkingSetSampler(), ProcessTelemetryStart?.WorkingSetBytes);
            CounterTotals = new Dictionary<string, long>(StringComparer.Ordinal);
            ObservedMaxima = new Dictionary<string, long>(StringComparer.Ordinal);
            ProjectRoot = Path.Join(options.OutputDirectory, "projects", reportId.ToString("N"));
            FixtureCopy = Path.Join(ProjectRoot, "fixture" + Path.GetExtension(options.FixturePath));
            BaselineProjectPath = Path.Join(ProjectRoot, "baseline", "project.trackdub");
            RunId = reportId;
        }

        public string? Stage { get; }
        public Guid ReportId { get; }
        public DateTimeOffset StartedAt { get; }
        public Stopwatch Clock { get; }
        public Dictionary<string, double?> Timings { get; }
        public List<BenchmarkStageResourceTelemetry> ResourceTelemetry { get; }
        public ResourceTelemetrySnapshot? ProcessTelemetryStart { get; }
        public IWorkingSetPeakMonitor? ProcessWorkingSetPeak { get; }
        public List<BenchmarkStageGarbageCollectionTelemetry> StageGarbageCollection { get; set; }
        public Dictionary<string, long> CounterTotals { get; }
        public Dictionary<string, long> ObservedMaxima { get; }
        public string ProjectRoot { get; }
        public string FixtureCopy { get; }
        public string BaselineProjectPath { get; }
        public string? PrimingProjectPath { get; set; }
        public bool HasPrerequisites { get; set; }
        public HeadlessDubbingHost? Host { get; set; }
        public IDisposable? CacheScope { get; set; }
        public bool OwnsHost { get; set; }
        public MeasuredRuns MeasuredRuns { get; set; } = null!;

        public string? FixtureHash { get; set; }
        public string? Reason { get; set; }
        public BenchmarkEvidenceStatus Status { get; set; } = BenchmarkEvidenceStatus.Failed;
        public IReadOnlyList<BenchmarkEvidenceStage> Stages { get; set; } = [];
        public Guid RunId { get; set; }
        public string? ActualModel { get; set; }
        public string? ActualProvider { get; set; }
    }

    private sealed record MeasuredRuns(
        Dictionary<string, List<double>> StageSamples,
        Dictionary<string, List<ResourceTelemetryDelta>> StageMemorySamples,
        List<double> PipelineSamples,
        Dictionary<string, List<double>> PhaseSamples,
        Dictionary<string, List<double>> FirstOutputSamples,
        DubbingRunResult LastResult,
        StageTimingCollector LastClock,
        string LastProjectPath);
}
