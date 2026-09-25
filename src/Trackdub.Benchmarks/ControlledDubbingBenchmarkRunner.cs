using System.Diagnostics;
using System.Security.Cryptography;
using Trackdub.Application.Dubbing;
using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Contracts.Benchmarking;
using Trackdub.Contracts.Dubbing;
using Trackdub.Contracts.Persistence;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;
using Trackdub.Inference.Runtime.ModelManifest;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Settings;
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
        var memory = new Dictionary<string, long?>(StringComparer.Ordinal)
        {
            ["processWorkingSetStart"] = Process.GetCurrentProcess().WorkingSet64,
            ["processWorkingSetEnd"] = null,
            ["managedAllocatedBytes"] = null,
            ["gpuDedicatedBytes"] = null,
        };
        string? fixtureHash = null;
        string? reason = null;
        BenchmarkEvidenceStatus status = BenchmarkEvidenceStatus.Failed;
        IReadOnlyList<BenchmarkEvidenceStage> stages = [];
        Guid runId = reportId;
        string? actualModel = null;
        string? actualProvider = null;
        string projectRoot = Path.Combine(options.OutputDirectory, "projects", reportId.ToString("N"));
        string fixtureCopy = Path.Combine(projectRoot, "fixture" + Path.GetExtension(options.FixturePath));
        string projectPath = Path.Combine(projectRoot, "project.trackdub");
        HeadlessDubbingHost? host = null;
        IDisposable? cacheScope = null;
        bool ownsHost = false;
        long allocatedStart = GC.GetTotalAllocatedBytes(precise: true);
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
                string cache = Path.Combine(projectRoot, "engine-cache");
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

            if (stage is not null)
            {
                IReadOnlyList<string> prerequisites = PrerequisitesFor(stage);
                if (prerequisites.Count > 0)
                {
                    long prerequisiteStart = Stopwatch.GetTimestamp();
                    DubbingRunResult preparation = await ExecuteAsync(
                        host, fixtureCopy, projectPath, options, prerequisites, true, cancellationToken).ConfigureAwait(false);
                    timings["prerequisites"] = Stopwatch.GetElapsedTime(prerequisiteStart).TotalMilliseconds;
                    RequirePreparationSucceeded(
                        preparation, "Prerequisite preparation did not complete successfully.",
                        out reason, out status, out stages);

                    // A stage already run by its prerequisites is timed as an in-place re-run
                    // (for ASR: re-transcribing existing segments), not as the stage itself.
                    // No prerequisite regenerates a later stage today, but guard against one
                    // sneaking the timed stage in (e.g. an opt-in stage running during import).
                    RunArtifacts prepared = await ReadRunArtifactsAsync(host, projectPath, options, cancellationToken)
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
                long warmupStart = Stopwatch.GetTimestamp();
                DubbingRunResult warmup = await ExecuteAsync(
                    host, fixtureCopy, projectPath, options, filter, true, cancellationToken).ConfigureAwait(false);
                timings["warmup"] = Stopwatch.GetElapsedTime(warmupStart).TotalMilliseconds;
                RequirePreparationSucceeded(
                    warmup, "Warm-host preparation did not complete successfully.",
                    out reason, out status, out stages);
            }
            if (options.Mode == "artifact-resume")
            {
                DubbingRunResult priming = await ExecuteAsync(
                    host, fixtureCopy, projectPath, options, filter, true, cancellationToken).ConfigureAwait(false);
                if (priming.OverallStatus != DubbingRunStatus.Succeeded)
                {
                    reason = "Artifact-resume preparation did not complete successfully.";
                    status = BenchmarkEvidenceStatus.Skipped;
                    stages = MapStages(priming, [], null, null);
                    throw new PreparationIncompleteException();
                }
            }

            long runStart = Stopwatch.GetTimestamp();
            var stageClock = new StageTimingCollector(runStart);
            var phases = new BenchmarkPhaseCapture();
            DubbingRunResult result;
            using (BenchmarkPhaseCapture.Activate(phases))
            {
                result = await ExecuteAsync(
                    host, fixtureCopy, projectPath, options, filter, options.Mode != "artifact-resume", cancellationToken,
                    stageClock).ConfigureAwait(false);
            }
            timings["pipeline"] = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
            foreach ((string name, double? duration) in phases.SnapshotMilliseconds())
                timings[name] = duration;
            timings["import"] = timings.GetValueOrDefault("phase:import");
            timings["preflight"] = timings.GetValueOrDefault("phase:preflight");
            timings["export"] = stageClock.GetMilliseconds("Export");
            runId = result.RunId;
            RunArtifacts artifacts = await ReadRunArtifactsAsync(host, projectPath, options, cancellationToken)
                .ConfigureAwait(false);
            stages = MapStages(result, artifacts.StageRuns, options.Model, stageClock);
            if (artifacts.HasUsableTranscript)
                timings["firstUsableTranscript"] = stageClock.GetCompletionMilliseconds("Asr");
            if (artifacts.HasPlayableTake)
                timings["firstPlayableAudio"] = stageClock.GetCompletionMilliseconds("Tts");
            BenchmarkEvidenceStage? requestedStage = stage is null
                ? null
                : stages.LastOrDefault(x => x.Name.Equals(stage, StringComparison.OrdinalIgnoreCase));
            actualModel = requestedStage?.ActualModel;
            actualProvider = requestedStage?.ActualProvider;
            if (stage is not null && requestedStage?.Status != BenchmarkEvidenceStatus.Completed)
            {
                status = requestedStage?.Status ?? BenchmarkEvidenceStatus.Skipped;
                reason = requestedStage?.Reason
                    ?? (result.PreFlightFailures is { Count: > 0 }
                        ? "Preflight failed: " + string.Join("; ", result.PreFlightFailures)
                        : "Requested stage produced no successful outcome.");
            }
            else if (result.OverallStatus != DubbingRunStatus.Succeeded)
            {
                status = result.OverallStatus == DubbingRunStatus.PartialSuccess
                    ? BenchmarkEvidenceStatus.PartiallyCompleted
                    : BenchmarkEvidenceStatus.Failed;
                reason = result.PreFlightFailures is { Count: > 0 }
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
            MeasuredPipelineResult measured = await MeasurePipelineAsync(
                host, fixtureCopy, projectPath, options, stage, filter, timings, cancellationToken).ConfigureAwait(false);
            runId = measured.RunId;
            stages = measured.Stages;
            actualModel = measured.ActualModel;
            actualProvider = measured.ActualProvider;
            status = measured.Status;
            reason = measured.Reason;
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
            memory["processWorkingSetEnd"] = Process.GetCurrentProcess().WorkingSet64;
            memory["managedAllocatedBytes"] = GC.GetTotalAllocatedBytes(precise: true) - allocatedStart;
            timings["total"] = clock.Elapsed.TotalMilliseconds;
            var configuration = new Dictionary<string, string>
            {
                ["targetLanguage"] = options.TargetLanguage,
                ["sourceLanguage"] = options.SourceLanguage ?? "auto",
                ["stage"] = stage ?? "all",
                ["hardware"] = BenchmarkHardwareInfo.Capture(),
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
            };
            using var saveTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            CancellationToken saveToken = status == BenchmarkEvidenceStatus.Canceled
                ? saveTimeout.Token
                : cancellationToken;
            await _history.SaveAsync(report, saveToken).ConfigureAwait(false);
            return report;
        }
    }

    private async Task<MeasuredPipelineResult> MeasurePipelineAsync(
        HeadlessDubbingHost host,
        string fixtureCopy,
        string projectPath,
        ControlledDubbingBenchmarkOptions options,
        string? stage,
        IReadOnlyList<string>? filter,
        Dictionary<string, double?> timings,
        CancellationToken cancellationToken)
    {
        long runStart = Stopwatch.GetTimestamp();
        var stageClock = new StageTimingCollector(runStart);
        var phases = new BenchmarkPhaseCapture();
        DubbingRunResult result;
        using (BenchmarkPhaseCapture.Activate(phases))
        {
            result = await ExecuteAsync(
                host, fixtureCopy, projectPath, options, filter, options.Mode != "artifact-resume", cancellationToken,
                stageClock).ConfigureAwait(false);
        }

        timings["pipeline"] = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
        foreach ((string name, double? duration) in phases.SnapshotMilliseconds())
        {
            timings[name] = duration;
        }

        timings["import"] = timings.GetValueOrDefault("phase:import");
        timings["preflight"] = timings.GetValueOrDefault("phase:preflight");
        timings["export"] = stageClock.GetMilliseconds("Export");
        RunArtifacts artifacts = await ReadRunArtifactsAsync(host, projectPath, options, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<BenchmarkEvidenceStage> stages = MapStages(result, artifacts.StageRuns, options.Model, stageClock);
        if (artifacts.HasUsableTranscript)
        {
            timings["firstUsableTranscript"] = stageClock.GetCompletionMilliseconds("Asr");
        }

        if (artifacts.HasPlayableTake)
        {
            timings["firstPlayableAudio"] = stageClock.GetCompletionMilliseconds("Tts");
        }

        BenchmarkEvidenceStage? requestedStage = stage is null
            ? null
            : stages.LastOrDefault(x => x.Name.Equals(stage, StringComparison.OrdinalIgnoreCase));
        string? actualModel = requestedStage?.ActualModel;
        string? actualProvider = requestedStage?.ActualProvider;
        (BenchmarkEvidenceStatus status, string? reason) = DetermineOutcome(
            stage, requestedStage, result, stages, options.Provider, actualProvider);
        return new MeasuredPipelineResult(result.RunId, stages, actualModel, actualProvider, status, reason);
    }

    private static (BenchmarkEvidenceStatus Status, string? Reason) DetermineOutcome(
        string? stage,
        BenchmarkEvidenceStage? requestedStage,
        DubbingRunResult result,
        IReadOnlyList<BenchmarkEvidenceStage> stages,
        string? requestedProvider,
        string? actualProvider)
    {
        if (stage is not null && requestedStage?.Status != BenchmarkEvidenceStatus.Completed)
        {
            return (requestedStage?.Status ?? BenchmarkEvidenceStatus.Skipped,
                requestedStage?.Reason ?? "Requested stage produced no successful outcome.");
        }

        if (result.OverallStatus != DubbingRunStatus.Succeeded)
        {
            BenchmarkEvidenceStatus status = result.OverallStatus == DubbingRunStatus.PartialSuccess
                ? BenchmarkEvidenceStatus.PartiallyCompleted
                : BenchmarkEvidenceStatus.Failed;
            string reason = result.PreFlightFailures is { Count: > 0 }
                ? "Preflight failed."
                : string.Join("; ", stages
                    .Where(measured => measured.Status != BenchmarkEvidenceStatus.Completed)
                    .Select(measured => $"{measured.Name}:{measured.Reason ?? measured.Status.ToString()}"));
            return (status, string.IsNullOrWhiteSpace(reason) ? "Pipeline did not complete successfully." : reason);
        }

        if (requestedProvider is not null &&
            (actualProvider is null || !BenchmarkComparison.ProviderMatches(requestedProvider, actualProvider)))
        {
            return (BenchmarkEvidenceStatus.PartiallyCompleted,
                "Actual provider was unavailable or differed from requested provider.");
        }

        return (BenchmarkEvidenceStatus.Completed, null);
    }

    private sealed record MeasuredPipelineResult(
        Guid RunId,
        IReadOnlyList<BenchmarkEvidenceStage> Stages,
        string? ActualModel,
        string? ActualProvider,
        BenchmarkEvidenceStatus Status,
        string? Reason);

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
        if (options.Provider is not null &&
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
        return DubbingPipelineStages.ExtendedStageOrder.SingleOrDefault(x =>
            x.Equals(requested, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentException("Unknown stage.", nameof(requested));
    }

    private static IReadOnlyList<string> PrerequisitesFor(string stage)
    {
        int index = DubbingPipelineStages.ExtendedStageOrder.ToList().FindIndex(x =>
            x.Equals(stage, StringComparison.OrdinalIgnoreCase));
        if (index <= 0) return [];
        return DubbingPipelineStages.ExtendedStageOrder.Take(index)
            .Where(x => DubbingPipelineStages.DefaultStageOrder.Contains(x, StringComparer.OrdinalIgnoreCase))
            .ToArray();
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
        return HeadlessDubbingHost.Create(new HeadlessTrackdubOptions
        {
            ModelDirectory = options.ModelDirectory,
            FfmpegPath = options.FfmpegPath,
            FfprobePath = options.FfprobePath,
            HardwareOverrides = pins,
            RequirePreferredExecutionProviders = pins is not null,
            ServiceConfigurator = _serviceConfigurator,
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
                File.Exists(Path.Combine(project, artifact.RelativePath))));
        return new RunArtifacts(
            rawStageRuns,
            state.TranscriptSegments.Any(segment => !string.IsNullOrWhiteSpace(segment.Text)),
            playableTake);
    }

    private static IReadOnlyList<BenchmarkEvidenceStage> MapStages(
        DubbingRunResult result, IReadOnlyList<StageRunRecord> records, string? requestedModel,
        StageTimingCollector? stageClock) =>
        result.StageOutcomes.Select(outcome =>
        {
            StageRunRecord? record = records.LastOrDefault(x =>
                x.StageName.Equals(outcome.StageName, StringComparison.OrdinalIgnoreCase) &&
                x.StartedAtUtc >= result.StartTime.AddSeconds(-1));
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
                DurationMilliseconds = stageClock?.GetMilliseconds(outcome.StageName),
                RequestedModel = requestedModel,
                ActualModel = record?.RuntimeInfo?.ModelAlias ?? record?.RuntimeInfo?.ModelId,
                RequestedProvider = record?.RuntimeInfo?.RequestedProvider,
                ActualProvider = record?.RuntimeInfo?.SelectedProvider,
            };
        }).ToArray();

    private sealed record RunArtifacts(
        IReadOnlyList<StageRunRecord> StageRuns, bool HasUsableTranscript, bool HasPlayableTake);

    private sealed class StageTimingCollector(long runStart) : IProgress<PipelineProgressEvent>
    {
        private readonly Dictionary<string, long> _starts = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _durations = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, double> _completions = new(StringComparer.OrdinalIgnoreCase);

        public void Report(PipelineProgressEvent value)
        {
            if (value.EventKind == PipelineProgressEventKind.Started)
                _starts[value.StageKey] = Stopwatch.GetTimestamp();
            else if ((value.EventKind is PipelineProgressEventKind.Completed or
                      PipelineProgressEventKind.Failed or PipelineProgressEventKind.Skipped) &&
                     _starts.TryGetValue(value.StageKey, out long start))
            {
                _durations[value.StageKey] = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _completions[value.StageKey] = Stopwatch.GetElapsedTime(runStart).TotalMilliseconds;
            }
        }

        public double? GetMilliseconds(string stage) =>
            _durations.TryGetValue(stage, out double milliseconds) ? milliseconds : null;

        public double? GetCompletionMilliseconds(string stage) =>
            _completions.TryGetValue(stage, out double milliseconds) ? milliseconds : null;
    }

    private sealed class EnvironmentOverride(string name, string value) : IDisposable
    {
        private readonly string? _previous = Environment.GetEnvironmentVariable(name);
        public void Dispose() => Environment.SetEnvironmentVariable(name, _previous);
        public void Apply() => Environment.SetEnvironmentVariable(name, value);
    }

    private sealed class PreparationIncompleteException : Exception;
}
