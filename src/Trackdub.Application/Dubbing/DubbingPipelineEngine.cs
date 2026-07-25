using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Contracts.Pipeline;
using Trackdub.Application.LipSync;
using Trackdub.Application.LipSynthesis;
using Trackdub.Application.Pipeline;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Pipeline;
using Trackdub.Domain.StageRuns;
using Microsoft.Extensions.DependencyInjection;
using System.Runtime.CompilerServices;
using System.Threading.Channels;

using Trackdub.Contracts.Dubbing;

namespace Trackdub.Application.Dubbing;

/// <summary>
/// Primary entry point for SDK consumers to execute the dubbing pipeline.
/// Orchestrates validation, pre-flight checks, stage execution, and result aggregation.
/// </summary>
public sealed class DubbingPipelineEngine : IDubbingPipelineEngine, ITransientFaultReporting
{
    private readonly IDubbingSessionFactory _sessionFactory;
    private readonly PipelineTransientFaultBus _transientFaultBus;

    /// <summary>
    /// The bus instance this engine publishes to. Exposed as <c>internal</c> so the
    /// test/Composition surface can pin identity against the Composition-registered
    /// singleton (spec §4.4 C8 follow-up) without resorting to reflection.
    /// </summary>
    internal PipelineTransientFaultBus TransientFaultBus => _transientFaultBus;

    internal static string? NormalizeAsrSourceLanguageCode(string? sourceLanguageCode) =>
        TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(sourceLanguageCode);

    /// <summary>
    /// Creates a new <see cref="DubbingPipelineEngine"/> backed by the given session factory.
    /// </summary>
    /// <param name="sessionFactory">Factory used to create per-run sessions.</param>
    /// <param name="transientFaultBus">
    /// Optional shared bus for transient-fault telemetry. When null the engine owns
    /// its own bus internally so the <see cref="ITransientFaultReporting"/> surface
    /// is always observable. Callers that need shared visibility across engine +
    /// diagnostics exporter should DI-register the bus as a singleton.
    /// </param>
    public DubbingPipelineEngine(IDubbingSessionFactory sessionFactory, PipelineTransientFaultBus? transientFaultBus = null)
    {
        _sessionFactory = sessionFactory ?? throw new ArgumentNullException(nameof(sessionFactory));
        _transientFaultBus = transientFaultBus ?? new PipelineTransientFaultBus();
    }

    /// <summary>
    /// Executes the dubbing pipeline according to the provided options.
    /// </summary>
    /// <param name="options">Immutable configuration for this pipeline run.</param>
    /// <param name="progress">Optional progress reporter for stage-level events.</param>
    /// <param name="cancellationToken">Token to request clean abort between stages.</param>
    /// <returns>An immutable <see cref="DubbingRunResult"/> describing the outcome of every stage.</returns>
    public async Task<DubbingRunResult> ExecuteAsync(
        DubbingSessionOptions options,
        IProgress<PipelineProgressEvent>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);

        Guid runId = Guid.NewGuid();
        DateTimeOffset runStart = DateTimeOffset.UtcNow;
        Guid projectId = Guid.Empty;
        var stageOutcomes = new List<StageOutcome>();
        string[] stagesToRun = ResolveStageOrder(options.StageFilter);

        if (stagesToRun.Length == 0 && options.StageFilter is { Count: > 0 })
        {
            return BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures: [$"StageFilter did not match any known pipeline stage: {string.Join(", ", options.StageFilter)}"]);
        }

        string[] stagesMissingTargetLanguage = stagesToRun
            .Where(DubbingPipelineStages.RequiresTargetLanguage)
            .ToArray();
        if (string.IsNullOrWhiteSpace(options.TargetLanguageCode)
            && stagesMissingTargetLanguage.Length > 0)
        {
            return BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures:
                [$"Target language is required for stage(s): {string.Join(", ", stagesMissingTargetLanguage)}."]);
        }

        // --- Validation: media file must exist ONLY if a stage in this run consumes it. ---
        // Stages such as Translation, Tts, and Export operate against cached artifacts in
        // the project directory and do not need the original source media file to be present.
        // For existing projects, resolve the stored source path from SQLite before failing.
        DubbingSessionOptions effectiveOptions = options;
        bool requiresSourceMedia = stagesToRun
            .Any(stage => DubbingPipelineStages.RequiresSourceMedia(stage));

        if (requiresSourceMedia
            && !File.Exists(options.SourceMediaPath)
            && options.ProjectOutputDirectory is not null)
        {
            DubbingProjectContext? projectContext = await DubbingProjectContextResolver
                .TryOpenAsync(_sessionFactory, options.ProjectOutputDirectory, cancellationToken)
                .ConfigureAwait(false);

            if (!string.IsNullOrWhiteSpace(projectContext?.SourceMediaPath))
            {
                effectiveOptions = options with { SourceMediaPath = projectContext.SourceMediaPath };
            }
        }

        if (requiresSourceMedia && !File.Exists(effectiveOptions.SourceMediaPath))
        {
            return BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures: [$"Media file not found: {effectiveOptions.SourceMediaPath}"]);
        }

        options = effectiveOptions;

        // --- Validation: create output directory if missing ---
        string projectOutputDirectory = options.ProjectOutputDirectory
            ?? Path.Combine(
                Path.GetDirectoryName(options.SourceMediaPath) ?? ".",
                Path.GetFileNameWithoutExtension(options.SourceMediaPath) + ".trackdub");

        if (!Directory.Exists(projectOutputDirectory))
        {
            Directory.CreateDirectory(projectOutputDirectory);
        }

        // --- Capture immutable ExecutionSnapshot ---
        Dictionary<string, string> executionSnapshot = CaptureExecutionSnapshot(options);

        // --- Create session ---
        IDubbingSession session;
        try
        {
            StudioSettings sessionSettings = StudioSettings.Default with
            {
                DefaultSourceLanguage = options.SourceLanguageCode,
                DefaultTargetLanguage = options.TargetLanguageCode,
            };
            session = _sessionFactory.CreateSession(projectOutputDirectory, sessionSettings);
        }
        catch (Exception ex)
        {
            return BuildErrorResult(runId, runStart, stageOutcomes,
                DubbingRunStatus.Failed,
                preFlightFailures: [$"Failed to create session: {ex.Message}"],
                executionSnapshot: executionSnapshot);
        }

        await using (session.ConfigureAwait(false))
        {
            TranscriptProjectState? initialProjectState = null;

            // --- Ensure project/media spine exists for fresh SDK runs ---
            if (requiresSourceMedia)
            {
                initialProjectState = await EnsureMediaSpineCreatedAsync(session, options, cancellationToken).ConfigureAwait(false);
            }

            // --- Capture projectId for transient-fault telemetry (spec §4.4 follow-up lane V2) ---
            // Without this hoist, per-project aggregation silently drops engine-emitted faults whose
            // ProjectId defaults to Guid.Empty (the bus's CountsByKindForProject rejects Empty).
            // Tie the load to the ensure-media-spine path so we don't OpenAsync three times per run.
            try
            {
                initialProjectState ??= await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
                projectId = initialProjectState.ProjectState.Project.Id;
            }
            // OperationCanceledException naturally propagates through filter-less try-blocks; no
            // explicit rethrow arm needed. Per AGENTS.md "Unnecessary try/catch blocks. Prefer to remove those."
            catch (Exception ex) when (IsProjectMissingException(ex))
            {
                // Best-effort: Snapshot() consumers (DiagnosticsBundleExporter) still receive engine-
                // emitted rows; per-project filter consumers see them as Guid.Empty which throws.
                projectId = Guid.Empty;
            }

            // --- Pre-flight checks ---
            DubbingRunResult? preFlightResult = await RunPreFlightChecksAsync(
                session,
                options,
                stagesToRun,
                runId,
                runStart,
                stageOutcomes,
                executionSnapshot,
                cancellationToken).ConfigureAwait(false);
            if (preFlightResult is not null)
            {
                return preFlightResult;
            }

            // Reload selections after pre-flight so starter-pack or settings changes are visible to execution.
            RuntimeModelSelections runtimeSelections = await CreateRuntimeSelectionsAsync(
                session,
                options,
                cancellationToken).ConfigureAwait(false);

            MergeRuntimeModelSelectionsIntoSnapshot(
                executionSnapshot,
                runtimeSelections,
                TryResolveService<IModelAliasResolver>(session));

            // --- Execute stages ---
            string? failedPrerequisiteStage = null;

            foreach (string stageName in stagesToRun)
            {
                // Check cancellation between stages
                if (cancellationToken.IsCancellationRequested)
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, "CANCELLED"));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped, "Cancelled");
                    continue;
                }

                // Skip if a prerequisite stage failed
                if (failedPrerequisiteStage is not null)
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, StageSkipReasonCodes.PrerequisiteFailed));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped,
                        $"Skipped due to failed prerequisite: {failedPrerequisiteStage}");
                    continue;
                }

                // Check resumability: skip stages with valid existing artifacts
                if (!options.ForceRerun &&
                    await HasValidExistingArtifactsAsync(
                        session,
                        options,
                        stageName,
                        executionSnapshot,
                        cancellationToken).ConfigureAwait(false))
                {
                    stageOutcomes.Add(BuildSkippedOutcome(stageName, StageSkipReasonCodes.ExistingArtifactsValid));
                    ReportProgress(progress, stageName, PipelineProgressEventKind.Skipped,
                        "Skipped — valid artifacts from prior run");
                    continue;
                }

                // Execute the stage
                StageOutcome outcome = await ExecuteStageAsync(
                    session,
                    options,
                    stageName,
                    executionSnapshot,
                    runtimeSelections,
                    progress,
                    projectId,
                    cancellationToken).ConfigureAwait(false);
                stageOutcomes.Add(outcome);

                if (outcome.Status == StageStatus.Failed && DubbingPipelineStages.PrerequisiteStages.Contains(stageName))
                {
                    failedPrerequisiteStage = stageName;
                }
            }

            if (ShouldRunPostLipSynthesisExport(stagesToRun, stageOutcomes))
            {
                int priorExportIndex = stageOutcomes.FindLastIndex(static outcome =>
                    string.Equals(outcome.StageName, StageNames.Export, StringComparison.OrdinalIgnoreCase));
                if (priorExportIndex >= 0)
                {
                    stageOutcomes.RemoveAt(priorExportIndex);
                }

                StageOutcome postLipExportOutcome = await ExecuteStageAsync(
                    session,
                    options,
                    StageNames.Export,
                    executionSnapshot,
                    runtimeSelections,
                    progress,
                    projectId,
                    cancellationToken).ConfigureAwait(false);
                stageOutcomes.Add(postLipExportOutcome);
            }

            // --- Build final result ---
            DubbingRunStatus overallStatus = DetermineOverallStatus(stageOutcomes);
            return new DubbingRunResult
            {
                RunId = runId,
                StartTime = runStart,
                EndTime = DateTimeOffset.UtcNow,
                OverallStatus = overallStatus,
                StageOutcomes = stageOutcomes.AsReadOnly(),
                ExecutionSnapshot = executionSnapshot.AsReadOnly(),
            };
        }
    }

    /// <summary>
    /// Runs pre-flight checks for all stages that will execute.
    /// Uses <see cref="IPipelineReadinessService"/> when available to evaluate all stages
    /// upfront, auto-provision downloadable models before the stage loop, and fail fast
    /// with an aggregated error list instead of discovering failures mid-run.
    /// Falls back to <see cref="IPipelinePreFlightChecker"/> when the readiness service
    /// is not registered (backward-compatible).
    /// </summary>
    private static async Task<DubbingRunResult?> RunPreFlightChecksAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stagesToRun,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot,
        CancellationToken cancellationToken)
    {
        if (session.Workspace.Project is null)
        {
            return null; // No project yet; pre-flight not applicable.
        }

        RuntimeModelSelections preFlightSelections = await CreateRuntimeSelectionsAsync(
            session,
            options,
            cancellationToken).ConfigureAwait(false);
        MergeRuntimeModelSelectionsIntoSnapshot(
            executionSnapshot,
            preFlightSelections,
            TryResolveService<IModelAliasResolver>(session));

        // ── New path: IPipelineReadinessService ───────────────────────────────
        IPipelineReadinessService? readinessService = TryResolveService<IPipelineReadinessService>(session);
        RuntimeModelSetupCoordinator? coordinator = TryResolveService<RuntimeModelSetupCoordinator>(session);

        if (readinessService is not null && coordinator is not null)
        {
            return await RunPreFlightWithReadinessServiceAsync(
                session,
                options,
                stagesToRun,
                runId,
                runStart,
                stageOutcomes,
                executionSnapshot,
                preFlightSelections,
                readinessService,
                coordinator,
                cancellationToken)
                .ConfigureAwait(false);
        }

        // ── Legacy fallback: IPipelinePreFlightChecker ────────────────────────
        IPipelinePreFlightChecker? checker = TryResolveService<IPipelinePreFlightChecker>(session);
        if (checker is null)
        {
            return null;
        }

        var failures = new List<string>();

        foreach (string stageName in stagesToRun)
        {
            if (ShouldSkipModelPreFlight(stageName, options.ModelPreferences))
                continue;

            if (!options.ForceRerun &&
                await HasValidExistingArtifactsAsync(
                    session, options, stageName, executionSnapshot, cancellationToken).ConfigureAwait(false))
                continue;

            try
            {
                await checker.EnsureModelsAvailableAsync(
                    stageName,
                    cancellationToken,
                    string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase)
                        ? options.SourceLanguageCode
                        : null).ConfigureAwait(false);
            }
            catch (RequiredModelNotAvailableException ex)
            {
                // Legacy: auto-downloadable VAD/ASR/Diar still deferred to stage execution.
                bool deferredStage =
                    string.Equals(stageName, StageNames.Vad, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stageName, StageNames.Asr, StringComparison.OrdinalIgnoreCase) ||
                    string.Equals(stageName, StageNames.Diarization, StringComparison.OrdinalIgnoreCase);
                if (!ex.CanAutoDownload || !deferredStage)
                    failures.Add($"{stageName}: {ex.ModelId}");
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // Catch any other unexpected exception during pre-flight model check
                // and record it as a failure for this stage
                failures.Add($"{stageName}: {ex.Message}");
            }
        }

        return failures.Count > 0
            ? BuildErrorResult(
                runId,
                runStart,
                stageOutcomes,
                DubbingRunStatus.PreFlightFailed,
                failures,
                executionSnapshot)
            : null;
    }

    /// <summary>
    /// Pre-flight using the consolidated readiness gate.
    /// Evaluates all stages upfront, auto-provisions downloadable models before the stage loop,
    /// and fails fast with an aggregated error if anything is still blocking.
    /// </summary>
    private static async Task<DubbingRunResult?> RunPreFlightWithReadinessServiceAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string[] stageNames,
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        Dictionary<string, string> executionSnapshot,
        RuntimeModelSelections selections,
        IPipelineReadinessService readinessService,
        RuntimeModelSetupCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        // Map to RuntimeStage, skipping stages with valid existing artifacts (resumable).
        var enabledStages = new List<RuntimeStage>();
        foreach (string stageName in stageNames)
        {
            RuntimeStage? stage = MapStageNameToRuntimeStage(stageName);
            if (stage is null) continue;

            if (!options.ForceRerun &&
                await HasValidExistingArtifactsAsync(
                    session, options, stageName, executionSnapshot, cancellationToken).ConfigureAwait(false))
                continue;

            enabledStages.Add(stage.Value);
        }

        if (enabledStages.Count == 0)
            return null;

        // Evaluate — state=null means no resume detection here (handled above via artifact check).
        PipelineReadinessReport report = await readinessService
            .EvaluateAsync(
                enabledStages,
                selections,
                state: null,
                cancellationToken,
                options.SourceLanguageCode,
                options.TargetLanguageCode)
            .ConfigureAwait(false);

        // Auto-provision downloadable models upfront. No dialogs — headless callbacks.
        if (report.Stages.Any(s => s.Status == ReadinessState.DownloadRequired))
        {
            RuntimeModelSetupCallbacks headlessCallbacks = BuildHeadlessCallbacks(cancellationToken);
            RuntimeModelSetupResult provisionResult = await coordinator
                .EnsurePipelineModelsAvailableAsync(
                    session.Workspace,
                    selections,
                    report,
                    headlessCallbacks,
                    options.SourceLanguageCode,
                    options.TargetLanguageCode,
                    cancellationToken)
                .ConfigureAwait(false);

            if (!provisionResult.IsReady)
            {
                return BuildErrorResult(
                    runId,
                    runStart,
                    stageOutcomes,
                    DubbingRunStatus.PreFlightFailed,
                    ["Model provisioning was cancelled during pre-flight."],
                    executionSnapshot);
            }

            // Re-evaluate after provisioning with refreshed selections in case settings changed.
            selections = await CreateRuntimeSelectionsAsync(session, options, cancellationToken)
                .ConfigureAwait(false);
            MergeRuntimeModelSelectionsIntoSnapshot(
                executionSnapshot,
                selections,
                TryResolveService<IModelAliasResolver>(session));
            readinessService.InvalidateCache(enabledStages);
            report = await readinessService
                .EvaluateAsync(
                    enabledStages,
                    selections,
                    state: null,
                    cancellationToken,
                    options.SourceLanguageCode,
                    options.TargetLanguageCode)
                .ConfigureAwait(false);
        }

        // Collect all remaining blocking states into an aggregated failure list.
        var failures = report.BlockingStages
            .Select(s => FormatPreFlightFailure(s))
            .ToList();

        return failures.Count > 0
            ? BuildErrorResult(
                runId,
                runStart,
                stageOutcomes,
                DubbingRunStatus.PreFlightFailed,
                failures,
                executionSnapshot)
            : null;
    }

    private static string FormatPreFlightFailure(StageReadiness s) =>
        s.Status switch
        {
            ReadinessState.CloudKeyMissing =>
                $"{s.StageName}: API key not configured for cloud provider" +
                (s.ModelAlias is not null ? $" ({s.ModelAlias})" : string.Empty),
            ReadinessState.ImportRequired =>
                $"{s.StageName}: model requires manual import" +
                (s.ModelId is not null ? $" — {s.ModelId}" : string.Empty),
            ReadinessState.CommercialBlocked =>
                $"{s.StageName}: selected model is non-commercial only",
            ReadinessState.IntegrityFailed =>
                $"{s.StageName}: model checksum verification failed — re-download the model",
            ReadinessState.ConsentRequired =>
                $"{s.StageName}: voice-clone consent required",
            _ =>
                s.Detail is not null
                    ? $"{s.StageName}: {s.Detail}"
                    : $"{s.StageName}: not ready ({s.Status})",
        };

    /// <summary>
    /// Headless provisioning callbacks: auto-download when possible, cancel otherwise.
    /// No UI dialogs, no file pickers.
    /// </summary>
    private static RuntimeModelSetupCallbacks BuildHeadlessCallbacks(CancellationToken cancellationToken) =>
        new(
            ResolveDecisionAsync: prompt => Task.FromResult(
                prompt.Status.CanAutoDownload
                    ? RuntimeModelSetupDecision.Download
                    : RuntimeModelSetupDecision.Cancel),
            PickImportFileAsync: () => Task.FromResult<string?>(null),
            CreateDownloadProgress: _ => new Progress<ModelDownloadProgress>(),
            RunOperationAsync: (op, _) => op(cancellationToken));

    internal static bool ShouldSkipModelPreFlight(
        string stageName,
        IReadOnlyDictionary<string, string>? modelPreferences) =>
        string.Equals(stageName, StageNames.Translation, StringComparison.OrdinalIgnoreCase) &&
        modelPreferences is not null &&
        modelPreferences.TryGetValue(StageNames.Translation, out string? modelAlias) &&
        TranslationModelOverrideSettings.IsDeepLModelAlias(modelAlias);

    /// <summary>
    /// Ensures the project's media spine (project record + normalized audio) exists.
    /// For fresh SDK runs the project has not been created yet; this creates it from the
    /// source media path without running any transcription stages.
    /// </summary>
    private static async Task<TranscriptProjectState?> EnsureMediaSpineCreatedAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        CancellationToken cancellationToken)
    {
        TranscriptWorkspace workspace = session.Workspace;
        try
        {
            return await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsProjectMissingException(ex))
        {
            // Project does not exist yet — create the media spine below.
        }

        string projectName = Path.GetFileNameWithoutExtension(options.SourceMediaPath);
        await workspace.CreateMediaSpineAsync(
            new CreateTranscriptProjectRequest(projectName, options.SourceMediaPath),
            cancellationToken).ConfigureAwait(false);

        return null;
    }

    private static bool IsProjectMissingException(Exception ex)
    {
        // ProjectMediaIngestService.OpenAsync throws this exact InvalidOperationException
        // when no project record exists yet.
        if (ex is InvalidOperationException &&
            ex.Message.Contains("does not contain a project record", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // On a brand-new SDK run the SQLite file can exist (created lazily on connection
        // open) before migrations have created the "projects" table — same "fresh project"
        // signal as above, just surfaced lower in the stack.
        if (string.Equals(ex.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal) &&
            ex.Message.Contains("no such table: projects", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Do not widen this further to bare exception types or loose message substrings:
        // those also fire for real failures (DB corruption, permission errors, unrelated
        // schema mismatches) and would silently misroute them into CreateMediaSpineAsync
        // instead of surfacing them.
        return false;
    }

    /// <summary>
    /// Attempts to resolve <see cref="IPipelinePreFlightChecker"/> from the session's DI scope.
    /// </summary>
    private static IPipelinePreFlightChecker? ResolvePreFlightChecker(IDubbingSession session) =>
        TryResolveService<IPipelinePreFlightChecker>(session);

    private static T? TryResolveService<T>(IDubbingSession session) where T : class
    {
        try { return session.Services.GetService<T>(); }
        catch (Exception) { return null; } // Intentionally generic: service resolution should never fail
    }

    private static RuntimeStage? MapStageNameToRuntimeStage(string stageName) =>
        stageName switch
        {
            StageNames.Vad => RuntimeStage.Vad,
            StageNames.Asr => RuntimeStage.Asr,
            StageNames.Diarization => RuntimeStage.Diarization,
            StageNames.Translation => RuntimeStage.Translation,
            StageNames.Tts => RuntimeStage.Tts,
            StageNames.Separation => RuntimeStage.Separation,
            StageNames.LipSync => RuntimeStage.LipSync,
            StageNames.LipSynthesis => RuntimeStage.LipSynthesis,
            _ => null,
        };

    private readonly record struct StageWorkflowResult(
        IReadOnlyList<string> ArtifactPaths,
        IReadOnlyList<string>? DegradationRecords,
        StageStatus Status = StageStatus.Succeeded,
        string? ReasonCode = null);

    /// <summary>
    /// Executes a single pipeline stage, wrapping it in timing and error handling.
    /// </summary>
    private async Task<StageOutcome> ExecuteStageAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string stageName,
        Dictionary<string, string> executionSnapshot,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        DateTimeOffset stageStart = DateTimeOffset.UtcNow;
        ReportProgress(progress, stageName, PipelineProgressEventKind.Started, null);

        try
        {
            StageWorkflowResult workflowResult = await RunStageWorkflowAsync(
                session,
                options,
                stageName,
                runtimeSelections,
                progress,
                cancellationToken).ConfigureAwait(false);

            DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
            PipelineProgressEventKind eventKind = workflowResult.Status switch
            {
                StageStatus.Skipped => PipelineProgressEventKind.Skipped,
                StageStatus.Failed => PipelineProgressEventKind.Failed,
                _ => PipelineProgressEventKind.Completed,
            };
            string? progressMessage = workflowResult.ReasonCode
                ?? workflowResult.DegradationRecords?.FirstOrDefault();
            ReportProgress(
                progress,
                stageName,
                eventKind,
                progressMessage,
                stageEnd - stageStart);

            return new StageOutcome
            {
                StageName = stageName,
                Status = workflowResult.Status,
                StartTime = stageStart,
                EndTime = stageEnd,
                ArtifactPaths = workflowResult.ArtifactPaths,
                DegradationRecords = workflowResult.DegradationRecords,
                ReasonCode = workflowResult.ReasonCode,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
            ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, "Cancelled", stageEnd - stageStart);

            return new StageOutcome
            {
                StageName = stageName,
                Status = StageStatus.Failed,
                StartTime = stageStart,
                EndTime = stageEnd,
                ArtifactPaths = [],
                ReasonCode = "CANCELLED",
            };
        }
        catch (Exception ex) when (TransientFailureClassifier.IsTransient(ex))
        {
            // Spec §4.4 publisher at the engine chokepoint: route transient exceptions
            // (filesystem locks, SQLite busy, HF mirror 5xx, model download errors) to the
            // bus surface so ITransientFaultReporting consumers see them. The engine itself
            // has no retry loop, so the stage outcome is still Failed here; callers wishing
            // to retry should wrap their stages in
            // <see cref="StageRunHelper.RunStageWithTransientRetryAsync{TResult}"/>.
            DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
            // projectId is hoisted once via the OpenAsync call above (see the
            // IsProjectMissingException catch near the top of this method) so per-project
            // aggregation gets a real id instead of falling back to Guid.Empty.
            PublishEngineTransient(projectId, stageName, TransientFailureClassifier.Classify(ex), ex, stageStart);
            ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, ex.Message, stageEnd - stageStart);

            return new StageOutcome
            {
                StageName = stageName,
                Status = StageStatus.Failed,
                StartTime = stageStart,
                EndTime = stageEnd,
                ArtifactPaths = [],
                ReasonCode = "STAGE_FAILED_TRANSIENT",
                DegradationRecords = [ex.Message],
            };
        }
        catch (Exception ex)
        {
            // Catch all exceptions from stage execution to ensure we always
            // return a StageOutcome rather than letting exceptions propagate.
            // This allows the pipeline to report failures gracefully.
            DateTimeOffset stageEnd = DateTimeOffset.UtcNow;
            ReportProgress(progress, stageName, PipelineProgressEventKind.Failed, ex.Message, stageEnd - stageStart);

            return new StageOutcome
            {
                StageName = stageName,
                Status = StageStatus.Failed,
                StartTime = stageStart,
                EndTime = stageEnd,
                ArtifactPaths = [],
                ReasonCode = "STAGE_FAILED",
                DegradationRecords = [ex.Message],
            };
        }
    }

    /// <summary>
    /// Dispatches execution to the appropriate workspace workflow method for the given stage.
    /// Returns artifact paths and any degradation records produced by the stage.
    /// </summary>
    private static async Task<StageWorkflowResult> RunStageWorkflowAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string stageName,
        RuntimeModelSelections runtimeSelections,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        TranscriptWorkspace workspace = session.Workspace;
        InferenceModelPreferences modelPreferences =
            RuntimeModelRequestFactory.CreateModelPreferences(runtimeSelections);

        switch (stageName)
        {
            case StageNames.Separation:
                TranscriptProjectState separationState = await workspace.RunStemSeparationAsync(
                    cancellationToken,
                    preferredModelAlias: runtimeSelections.SeparationModelAlias).ConfigureAwait(false);

                IReadOnlyList<string>? enhancementDegradations = ExtractSpeechEnhancementDegradations(separationState);
                if (enhancementDegradations is { Count: > 0 })
                {
                    // Surface the speech-enhancement failure before the separation Completed event so
                    // the caller sees it in the progress stream and the stage outcome DegradationRecords.
                    ReportProgress(progress, StageNames.SpeechEnhancement, PipelineProgressEventKind.Failed,
                        enhancementDegradations[0]);
                }

                return new StageWorkflowResult([], enhancementDegradations);

            case StageNames.Vad:
            case StageNames.Asr:
            case StageNames.Diarization:
                {
                    string? normalizedSourceLanguage = NormalizeAsrSourceLanguageCode(options.SourceLanguageCode);
                    await workspace.RunTranscriptStageAsync(
                        stageName,
                        enableSpeakerDiarization: string.Equals(
                            stageName,
                            StageNames.Diarization,
                            StringComparison.OrdinalIgnoreCase),
                        modelPreferences,
                        cancellationToken,
                        progress,
                        sourceLanguage: normalizedSourceLanguage).ConfigureAwait(false);
                    return new StageWorkflowResult([], null);
                }

            case StageNames.Translation:
                TranscriptProjectState translationState = await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (translationState.TranscriptSegments.Count == 0)
                {
                    return BuildNoTranscriptSegmentsSkip(stageName);
                }

                string sourceLanguage = options.SourceLanguageCode ?? "auto";
                await workspace.GenerateTranslationAsync(
                    new GenerateTranslationRequest(
                        SourceLanguage: sourceLanguage,
                        TargetLanguage: options.TargetLanguageCode,
                        PreferredModelAlias: runtimeSelections.TranslationModelAlias),
                    cancellationToken,
                    progress).ConfigureAwait(false);
                return new StageWorkflowResult([], null);

            case StageNames.Tts:
                TranscriptProjectState ttsState = await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
                if (ttsState.TranscriptSegments.Count == 0)
                {
                    return BuildNoTranscriptSegmentsSkip(stageName);
                }

                Dictionary<Guid, string>? fallbackVoiceIds = BuildUnattendedFallbackVoiceIds(
                    ttsState,
                    options.TargetLanguageCode);
                if (fallbackVoiceIds is { Count: > 0 })
                {
                    ReportProgress(
                        progress,
                        StageNames.Tts,
                        PipelineProgressEventKind.Progress,
                        $"Auto-assigning a fallback voice to {fallbackVoiceIds.Count} speaker(s) without a voice assignment (unattended run).");
                }

                await workspace.GenerateTtsForAllSpeakersAsync(
                    new GenerateTtsForAllSpeakersRequest(
                        FallbackVoiceIdsBySpeakerId: fallbackVoiceIds,
                        PreferredModelAlias: runtimeSelections.TtsModelAlias),
                    cancellationToken,
                    progress).ConfigureAwait(false);
                return new StageWorkflowResult([], null);

            case StageNames.Export:
                TranscriptProjectState state = await workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
                bool hasTranscriptSegments = state.TranscriptSegments.Count > 0;
                ExportOutputContainer container = ResolveExportContainer(options.ExportFormat);
                string outputPath = ResolveExportOutputPath(session.ProjectRootPath, container);
                if (!hasTranscriptSegments)
                {
                    ReportProgress(
                        progress,
                        StageNames.Export,
                        PipelineProgressEventKind.Progress,
                        "No transcript segments were detected; exporting source media without subtitles or dubbed speech.");
                }

                ExportStageResult exportResult = await workspace.ExportAsync(
                    state,
                    new ExportStageRequest(
                        ProjectId: state.ProjectState.Project.Id,
                        OutputPath: outputPath,
                        SubtitleFormats: hasTranscriptSegments ? [ExportSubtitleFormat.Srt] : [],
                        Container: container),
                    cancellationToken).ConfigureAwait(false);
                if (exportResult.IsBlocked)
                {
                    throw new InvalidOperationException(exportResult.BlockedReason ?? "Export blocked by tier gate.");
                }

                return new StageWorkflowResult(
                    [exportResult.OutputPath, exportResult.ExportVideoRelativePath],
                    null);

            case StageNames.LipSync:
                {
                    if (workspace.LipSync is null)
                    {
                        throw new InvalidOperationException(
                            "Lip-sync workflow is not available in this session. Ensure CompositionRoot registered LipSyncWorkflow.");
                    }

                    TranscriptProjectState lipSyncState = await workspace.RunLipSyncAsync(
                        new LipSyncAlignAllRequest(
                            PreferredModelAlias: runtimeSelections.LipSyncModelAlias),
                        cancellationToken).ConfigureAwait(false);
                    return BuildStageWorkflowResultFromStageRun(lipSyncState, StageNames.LipSync);
                }

            case StageNames.LipSynthesis:
                {
                    if (workspace.LipSynthesis is null)
                    {
                        throw new InvalidOperationException(
                            "Lip-synthesis workflow is not available in this session. Ensure CompositionRoot registered LipSynthesisWorkflow.");
                    }

                    (bool isLicenseApproved, bool allowExperimentalExecution) =
                        await ResolveLipSynthesisExecutionGatesAsync(
                            session,
                            runtimeSelections.LipSynthesisModelAlias,
                            cancellationToken).ConfigureAwait(false);

                    TranscriptProjectState lipSynthesisState = await workspace.RunLipSynthesisAsync(
                        new LipSynthesisRunRequest(
                            IsLicenseApproved: isLicenseApproved,
                            AllowExperimentalExecution: allowExperimentalExecution,
                            PreferredModelAlias: runtimeSelections.LipSynthesisModelAlias),
                        cancellationToken).ConfigureAwait(false);
                    return BuildStageWorkflowResultFromStageRun(lipSynthesisState, StageNames.LipSynthesis);
                }

            default:
                // Unknown stage — skip gracefully.
                return new StageWorkflowResult([], null);
        }
    }

    private static StageWorkflowResult BuildStageWorkflowResultFromStageRun(
        TranscriptProjectState state,
        string stageName)
    {
        (StageStatus status, string? reasonCode, IReadOnlyList<string>? degradations) =
            MapStageRunToSdkOutcome(GetLatestStageRun(state, stageName));

        return new StageWorkflowResult([], degradations, status, reasonCode);
    }

    private static StageWorkflowResult BuildNoTranscriptSegmentsSkip(string stageName) =>
        new(
            [],
            ["No transcript segments were detected; this stage has no work to perform."],
            StageStatus.Skipped,
            StageSkipReasonCodes.NoTranscriptSegments);

    internal static StageRunRecord? GetLatestStageRun(TranscriptProjectState state, string stageName) =>
        state.StageRuns
            .Where(r => string.Equals(r.StageName, stageName, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static r => r.StartedAtUtc)
            .FirstOrDefault();

    internal static (StageStatus Status, string? ReasonCode, IReadOnlyList<string>? Degradations)
        MapStageRunToSdkOutcome(StageRunRecord? stageRun) =>
        stageRun?.Status switch
        {
            StageRunStatus.Completed =>
                (StageStatus.Succeeded, null, null),
            StageRunStatus.Skipped =>
                (StageStatus.Skipped,
                    stageRun.FailureReason ?? "STAGE_SKIPPED",
                    null),
            StageRunStatus.Failed =>
                (StageStatus.Failed,
                    stageRun.FailureReason ?? "STAGE_FAILED",
                    null),
            StageRunStatus.PartiallyCompleted =>
                (StageStatus.Succeeded,
                    null,
                    stageRun.FailureReason is not null ? [stageRun.FailureReason] : null),
            StageRunStatus.Canceled =>
                (StageStatus.Failed,
                    stageRun.FailureReason ?? "CANCELLED",
                    null),
            StageRunStatus.Running =>
                (StageStatus.Failed,
                    "STAGE_INCOMPLETE",
                    ["Stage run did not reach a terminal state."]),
            null =>
                (StageStatus.Failed,
                    "STAGE_RUN_MISSING",
                    ["Stage completed without recording a stage run."]),
            _ =>
                (StageStatus.Failed,
                    stageRun.FailureReason ?? "STAGE_FAILED",
                    null),
        };

    // Speech-enhancement runs as an internal sub-step of RunStemSeparationAsync and is caught
    // there (fallback to unenhanced audio). Surface the failure so callers can include it in
    // StageOutcome.DegradationRecords and progress events rather than silently discarding it.
    internal static IReadOnlyList<string>? ExtractSpeechEnhancementDegradations(
        TranscriptProjectState state)
    {
        // Order first so we evaluate the *most recent* speech-enhancement attempt.
        // Filtering to Failed before ordering would surface stale failures even when
        // a later run succeeded, producing a false degraded outcome after a successful rerun.
        StageRunRecord? latestRun = state.StageRuns
            .Where(static r =>
                string.Equals(r.StageName, StageNames.SpeechEnhancement, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(static r => r.StartedAtUtc)
            .FirstOrDefault();

        if (latestRun is null || latestRun.Status != StageRunStatus.Failed)
        {
            return null;
        }

        string message = string.IsNullOrWhiteSpace(latestRun.FailureReason)
            ? "speech-enhancement failed; falling back to unenhanced audio"
            : $"speech-enhancement failed (falling back to unenhanced audio): {latestRun.FailureReason}";

        return [message];
    }

    /// <summary>
    /// Unattended runs have no voice-assignment step, so speakers without a deliberate
    /// (non-fallback) voice assignment would fail TTS outright. Mirrors the shell's fallback
    /// behavior: picks the first catalog voice whose language matches the dub target language,
    /// ordered by display name like the shell's voice picker. Speakers stay unassigned when no
    /// language-matching voice exists, so TTS fails with the explicit assignment error instead
    /// of dubbing with a wrong-language voice.
    /// </summary>
    internal static Dictionary<Guid, string>? BuildUnattendedFallbackVoiceIds(
        TranscriptProjectState state,
        string? targetLanguageCode)
    {
        if (state.Speakers.Count == 0 || state.AvailableVoices.Count == 0)
        {
            return null;
        }

        HashSet<Guid> deliberatelyAssignedSpeakerIds = state.VoiceAssignments
            .Where(static assignment => !assignment.IsFallback)
            .Select(static assignment => assignment.SpeakerId)
            .ToHashSet();

        string? defaultVoiceId = state.AvailableVoices
            .Where(voice => IsVoiceLanguageMatch(voice.LanguageCode, targetLanguageCode))
            .OrderBy(static voice => voice.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(static voice => voice.VoiceId)
            .FirstOrDefault();
        if (defaultVoiceId is null)
        {
            return null;
        }

        Dictionary<Guid, string> fallbackVoiceIds = state.Speakers
            .Where(speaker => !deliberatelyAssignedSpeakerIds.Contains(speaker.Id))
            .ToDictionary(static speaker => speaker.Id, _ => defaultVoiceId);
        return fallbackVoiceIds.Count > 0 ? fallbackVoiceIds : null;
    }

    private static bool IsVoiceLanguageMatch(string voiceLanguageCode, string? targetLanguageCode)
    {
        string? normalizedVoiceLanguage = NormalizeVoiceLanguageCode(voiceLanguageCode);
        string? normalizedTargetLanguage = NormalizeVoiceLanguageCode(targetLanguageCode);
        if (normalizedVoiceLanguage is null || normalizedTargetLanguage is null)
        {
            return true;
        }

        return string.Equals(normalizedVoiceLanguage, normalizedTargetLanguage, StringComparison.Ordinal) ||
               normalizedVoiceLanguage.StartsWith($"{normalizedTargetLanguage}-", StringComparison.Ordinal) ||
               normalizedTargetLanguage.StartsWith($"{normalizedVoiceLanguage}-", StringComparison.Ordinal);
    }

    private static string? NormalizeVoiceLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().Replace('_', '-').ToLowerInvariant();

    private static async Task<RuntimeModelSelections> CreateRuntimeSelectionsAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        CancellationToken cancellationToken)
    {
        StudioSettings settings = StudioSettings.Default;
        IServiceProvider? serviceProvider = session.Services;
        if (serviceProvider?.GetService<IStudioSettingsService>() is IStudioSettingsService settingsService)
        {
            settings = await settingsService.LoadAsync(cancellationToken).ConfigureAwait(false);
        }

        return RuntimeModelRequestFactory.CreateSelectionsFromSettings(
            settings,
            BuildModelPreferences(options));
    }

    /// <summary>
    /// Builds <see cref="InferenceModelPreferences"/> from the dubbing session options.
    /// </summary>
    private static InferenceModelPreferences? BuildModelPreferences(DubbingSessionOptions options)
    {
        bool hasModelOverrides = options.ModelPreferences is { Count: > 0 };
        if (!hasModelOverrides && !options.EnableAsrTextRefinement)
        {
            return null;
        }

        IReadOnlyDictionary<string, string> modelPreferences = options.ModelPreferences
            ?? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        return new InferenceModelPreferences(
            VadModelAlias: modelPreferences.GetValueOrDefault(StageNames.Vad),
            AsrModelAlias: modelPreferences.GetValueOrDefault(StageNames.Asr),
            DiarizationModelAlias: modelPreferences.GetValueOrDefault(StageNames.Diarization),
            SeparationModelAlias: modelPreferences.GetValueOrDefault(StageNames.Separation),
            TranslationModelAlias: modelPreferences.GetValueOrDefault(StageNames.Translation),
            TtsModelAlias: modelPreferences.GetValueOrDefault(StageNames.Tts),
            LipSyncModelAlias: modelPreferences.GetValueOrDefault(StageNames.LipSync),
            LipSynthesisModelAlias: modelPreferences.GetValueOrDefault(StageNames.LipSynthesis),
            EnableAsrTextRefinement: options.EnableAsrTextRefinement);
    }

    /// <summary>
    /// Merges resolved runtime model aliases (settings, starter packs, explicit preferences)
    /// into the execution snapshot so artifact resume checks compare against models that
    /// will actually execute.
    /// </summary>
    internal static void MergeRuntimeModelSelectionsIntoSnapshot(
        Dictionary<string, string> snapshot,
        RuntimeModelSelections selections,
        IModelAliasResolver? modelAliasResolver = null)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(selections);

        RuntimeModelRequestOptions options = RuntimeModelRequestFactory.CreateOptions(selections);

        SetSnapshotModelAlias(snapshot, StageNames.Asr, selections.AsrModelAlias);
        SetSnapshotModelVariant(snapshot, options, StageNames.Asr, RuntimeStage.Asr, selections.AsrModelAlias);
        AddModelId(snapshot, StageNames.Asr, selections.AsrModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Diarization, selections.DiarizationModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.Diarization,
            RuntimeStage.Diarization,
            selections.DiarizationModelAlias);
        AddModelId(snapshot, StageNames.Diarization, selections.DiarizationModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Separation, selections.SeparationModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.Separation,
            RuntimeStage.Separation,
            selections.SeparationModelAlias);
        AddModelId(snapshot, StageNames.Separation, selections.SeparationModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.OverlapRescue, selections.OverlapRescueModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.OverlapRescue,
            RuntimeStage.OverlapRescue,
            selections.OverlapRescueModelAlias);
        AddModelId(snapshot, StageNames.OverlapRescue, selections.OverlapRescueModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Translation, selections.TranslationModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.Translation,
            RuntimeStage.Translation,
            selections.TranslationModelAlias);
        AddModelId(snapshot, StageNames.Translation, selections.TranslationModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.Tts, selections.TtsModelAlias);
        SetSnapshotModelVariant(snapshot, options, StageNames.Tts, RuntimeStage.Tts, selections.TtsModelAlias);
        AddModelId(snapshot, StageNames.Tts, selections.TtsModelAlias, modelAliasResolver);

        if (selections.EnableAsrTextRefinement)
        {
            SetSnapshotModelAlias(snapshot, StageNames.TextRefinementAsr, selections.TextRefinementModelAlias);
            SetSnapshotModelVariant(
                snapshot,
                options,
                StageNames.TextRefinementAsr,
                RuntimeStage.TextRefinement,
                selections.TextRefinementModelAlias);
            AddModelId(snapshot, StageNames.TextRefinementAsr, selections.TextRefinementModelAlias, modelAliasResolver);
        }

        SetSnapshotModelAlias(snapshot, StageNames.LipSync, selections.LipSyncModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.LipSync,
            RuntimeStage.LipSync,
            selections.LipSyncModelAlias);
        AddModelId(snapshot, StageNames.LipSync, selections.LipSyncModelAlias, modelAliasResolver);

        SetSnapshotModelAlias(snapshot, StageNames.LipSynthesis, selections.LipSynthesisModelAlias);
        SetSnapshotModelVariant(
            snapshot,
            options,
            StageNames.LipSynthesis,
            RuntimeStage.LipSynthesis,
            selections.LipSynthesisModelAlias);
        AddModelId(snapshot, StageNames.LipSynthesis, selections.LipSynthesisModelAlias, modelAliasResolver);
    }

    private static void AddModelId(
        Dictionary<string, string> snapshot,
        string stageName,
        string? modelAlias,
        IModelAliasResolver? modelAliasResolver)
    {
        if (modelAliasResolver is not null &&
            !string.IsNullOrWhiteSpace(modelAlias) &&
            modelAliasResolver.TryResolveModelId(modelAlias, out string? modelId) &&
            !string.IsNullOrWhiteSpace(modelId))
        {
            snapshot[$"ModelId:{stageName}"] = modelId;
        }
    }

    private static void SetSnapshotModelAlias(
        Dictionary<string, string> snapshot,
        string stageName,
        string? modelAlias)
    {
        if (!string.IsNullOrWhiteSpace(modelAlias))
        {
            snapshot[$"Model:{stageName}"] = modelAlias;
        }
    }

    private static void SetSnapshotModelVariant(
        Dictionary<string, string> snapshot,
        RuntimeModelRequestOptions options,
        string stageName,
        RuntimeStage stage,
        string? modelAlias)
    {
        string? variant = RuntimeModelRequestFactory.ResolvePreferredModelVariantAlias(options, stage, modelAlias);
        if (!string.IsNullOrWhiteSpace(variant))
        {
            snapshot[$"ModelVariant:{stageName}"] = variant;
        }
    }

    /// <summary>
    /// Captures an immutable snapshot of provider/model/voice decisions at run start.
    /// </summary>
    private static Dictionary<string, string> CaptureExecutionSnapshot(DubbingSessionOptions options)
    {
        var snapshot = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SourceMediaPath"] = options.SourceMediaPath,
            ["TargetLanguageCode"] = options.TargetLanguageCode,
            ["ForceRerun"] = options.ForceRerun.ToString(),
            ["EnableAsrTextRefinement"] = options.EnableAsrTextRefinement.ToString(),
            ["ExportFormat"] = ExportContainerKey(ResolveExportContainer(options.ExportFormat)),
        };

        if (options.SourceLanguageCode is not null)
        {
            snapshot["SourceLanguageCode"] = options.SourceLanguageCode;
        }

        if (options.ModelPreferences is not null)
        {
            foreach ((string stage, string model) in options.ModelPreferences)
            {
                snapshot[$"Model:{stage}"] = model;
            }
        }

        if (options.VoiceAssignmentOverrides is not null)
        {
            foreach ((string speaker, string voice) in options.VoiceAssignmentOverrides)
            {
                snapshot[$"Voice:{speaker}"] = voice;
            }
        }

        return snapshot;
    }

    internal static ExportOutputContainer ResolveExportContainer(string? exportFormat) =>
        ExportStageRequestBuilder.ResolveContainer(exportFormat);

    internal static string ResolveExportOutputPath(string projectRootPath, ExportOutputContainer container)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRootPath);
        string extension = container == ExportOutputContainer.Mkv ? ".mkv" : ".mp4";
        return Path.Combine(projectRootPath, "exports", "dubbed" + extension);
    }

    private static string ExportContainerKey(ExportOutputContainer container) =>
        container == ExportOutputContainer.Mkv ? "mkv" : "mp4";

    /// <summary>
    /// Determines the ordered list of stages to execute, respecting an optional filter.
    /// Unfiltered runs use the speech/export default order. Filtered runs resolve against
    /// the extended stage order so lip-sync / lip-synthesis can be opted in.
    /// </summary>
    internal static string[] ResolveStageOrder(IReadOnlyList<string>? stageFilter)
    {
        if (stageFilter is null || stageFilter.Count == 0)
        {
            return [.. DubbingPipelineStages.DefaultStageOrder];
        }

        HashSet<string> filterSet = new(stageFilter, StringComparer.OrdinalIgnoreCase);
        return DubbingPipelineStages.ExtendedStageOrder.Where(s => filterSet.Contains(s)).ToArray();
    }

    /// <summary>
    /// Checks whether a stage has valid existing artifacts from a prior run
    /// that match the current execution snapshot.
    /// </summary>
    private static async Task<bool> HasValidExistingArtifactsAsync(
        IDubbingSession session,
        DubbingSessionOptions options,
        string stageName,
        Dictionary<string, string> currentSnapshot,
        CancellationToken cancellationToken)
    {
        IArtifactStore? artifactStore = session.Services.GetService<IArtifactStore>();
        if (artifactStore is null)
        {
            return false;
        }

        TranscriptProjectState state;
        try
        {
            state = await session.Workspace.Project.OpenAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
        catch (System.Security.SecurityException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (Exception ex) when (
            string.Equals(ex.GetType().FullName, "Microsoft.Data.Sqlite.SqliteException", StringComparison.Ordinal))
        {
            return false;
        }

        string? exportRelativePath = string.Equals(stageName, StageNames.Export, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(session.ProjectRootPath, ResolveExportOutputPath(
                session.ProjectRootPath,
                ResolveExportContainer(options.ExportFormat)))
            : null;

        return StageArtifactResumeEvaluator.CanResumeStage(
            state,
            artifactStore,
            stageName,
            currentSnapshot,
            session.ProjectRootPath,
            options.TargetLanguageCode,
            exportRelativePath);
    }

    /// <summary>
    /// Skip reason codes that represent intentional resume/prerequisite gating rather than
    /// a failed attempt to run a requested stage.
    /// </summary>
    internal static bool IsBenignSkipReasonCode(string? reasonCode) =>
        StageSkipReasonCodes.IsBenignSkip(reasonCode);

    internal static bool ShouldRunPostLipSynthesisExport(
        IReadOnlyList<string> stagesToRun,
        IReadOnlyList<StageOutcome> outcomes)
    {
        if (!stagesToRun.Any(static stage =>
                string.Equals(stage, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase))
            || !stagesToRun.Any(static stage =>
                string.Equals(stage, StageNames.Export, StringComparison.OrdinalIgnoreCase)))
        {
            return false;
        }

        StageOutcome? lipSynthesisOutcome = outcomes.LastOrDefault(static outcome =>
            string.Equals(outcome.StageName, StageNames.LipSynthesis, StringComparison.OrdinalIgnoreCase));

        return lipSynthesisOutcome?.Status == StageStatus.Succeeded;
    }

    private static async Task<(bool IsLicenseApproved, bool AllowExperimentalExecution)>
        ResolveLipSynthesisExecutionGatesAsync(
            IDubbingSession session,
            string? preferredModelAlias,
            CancellationToken cancellationToken)
    {
        IModelInventoryService? inventoryService = TryResolveService<IModelInventoryService>(session);
        if (inventoryService is null)
        {
            return (false, false);
        }

        IReadOnlyList<ModelInventoryEntry> inventory =
            await inventoryService.GetAllAsync(cancellationToken).ConfigureAwait(false);
        ModelInventoryEntry? entry =
            LipSynthesisInventoryGate.ResolveEntry(inventory, preferredModelAlias);

        return (
            LipSynthesisInventoryGate.IsLicenseApproved(entry),
            LipSynthesisInventoryGate.AllowExperimentalExecution(entry));
    }

    /// <summary>
    /// Determines the overall run status from individual stage outcomes.
    /// </summary>
    internal static DubbingRunStatus DetermineOverallStatus(List<StageOutcome> outcomes)
    {
        if (outcomes.Count == 0)
        {
            return DubbingRunStatus.Succeeded;
        }

        bool anyFailed = outcomes.Any(o => o.Status == StageStatus.Failed);
        bool anySucceeded = outcomes.Any(o => o.Status == StageStatus.Succeeded);
        bool allSucceeded = outcomes.All(o => o.Status == StageStatus.Succeeded);

        if (allSucceeded)
        {
            return DubbingRunStatus.Succeeded;
        }

        if (anyFailed && anySucceeded)
        {
            return DubbingRunStatus.PartialSuccess;
        }

        if (anyFailed)
        {
            return DubbingRunStatus.Failed;
        }

        bool anyNonBenignSkip = outcomes.Any(static o =>
            o.Status == StageStatus.Skipped
            && !IsBenignSkipReasonCode(o.ReasonCode));

        if (anyNonBenignSkip)
        {
            return anySucceeded ? DubbingRunStatus.PartialSuccess : DubbingRunStatus.Failed;
        }

        return DubbingRunStatus.Succeeded;
    }

    /// <summary>
    /// Reports a progress event to the optional progress reporter.
    /// </summary>
    private static void ReportProgress(
        IProgress<PipelineProgressEvent>? progress,
        string stageName,
        PipelineProgressEventKind eventKind,
        string? message,
        TimeSpan elapsed = default)
    {
        progress?.Report(new PipelineProgressEvent(
            StageName: stageName,
            EventKind: eventKind,
            Percentage: eventKind == PipelineProgressEventKind.Completed ? 100 : 0,
            Message: message,
            ElapsedDuration: elapsed));
    }

    /// <summary>
    /// Builds a <see cref="StageOutcome"/> for a skipped stage.
    /// </summary>
    private static StageOutcome BuildSkippedOutcome(string stageName, string reasonCode)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        return new StageOutcome
        {
            StageName = stageName,
            Status = StageStatus.Skipped,
            StartTime = now,
            EndTime = now,
            ArtifactPaths = [],
            ReasonCode = reasonCode,
        };
    }

    /// <summary>
    /// Builds a terminal <see cref="DubbingRunResult"/> for validation or pre-flight failures.
    /// </summary>
    private static DubbingRunResult BuildErrorResult(
        Guid runId,
        DateTimeOffset runStart,
        List<StageOutcome> stageOutcomes,
        DubbingRunStatus status,
        IReadOnlyList<string>? preFlightFailures = null,
        IReadOnlyDictionary<string, string>? executionSnapshot = null)
    {
        return new DubbingRunResult
        {
            RunId = runId,
            StartTime = runStart,
            EndTime = DateTimeOffset.UtcNow,
            OverallStatus = status,
            StageOutcomes = stageOutcomes.AsReadOnly(),
            PreFlightFailures = preFlightFailures,
            ExecutionSnapshot = executionSnapshot,
        };
    }

    /// <summary>
    /// Best-effort transient-fault publisher used by <see cref="ExecuteStageAsync"/>'s
    /// transient-exception arm. Wrapped in try/catch so telemetry can never break
    /// pipeline flow. Mirrors <see cref="StageRunHelper.PublishTransient"/>'s contract
    /// but skips stage-run persistence since the engine layer does not own that
    /// lifecycle. See <c>docs/internal/pipeline-readiness-spec.md</c> section 4.4.
    /// </summary>
    private void PublishEngineTransient(
        Guid projectId,
        string stageName,
        TransientFailureKind kind,
        Exception ex,
        DateTimeOffset stageStart)
    {
        try
        {
            var context = new Dictionary<string, string>
            {
                ["Engine"] = "DubbingPipelineEngine",
                ["StageStart"] = stageStart.ToString("O"),
            };
            if (ex.GetType().FullName is { Length: > 0 } typeName)
            {
                context["ExceptionType"] = typeName;
            }
            _transientFaultBus.Publish(new PipelineTransientFault(
                projectId, stageName, kind, ex.Message, DateTimeOffset.UtcNow, 1, context));
        }
        catch
        {
            // Telemetry must never break pipeline flow — bus surface is best-effort.
        }
    }

    /// <summary>
    /// Streams transient-fault records emitted during this engine's lifetime.
    /// Bridges <see cref="PipelineTransientFaultBus"/> (IObservable) into an
    /// <see cref="IAsyncEnumerable{T}"/> via a <see cref="Channel{T}"/> so subscribers
    /// do not have to take an Rx dependency. Drains snapshot (already replayed by bus
    /// Subscribe), then forwards live events until the consumer cancels. See
    /// <c>docs/internal/pipeline-readiness-spec.md</c> section 4.4.
    /// </summary>
    public async IAsyncEnumerable<PipelineTransientFault> TransientFaultsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Bounded 256-capacity ring with DropOldest. Reasoning: a stalled consumer (e.g.
        // UI shell paused on UI thread) must not OOM the process. Stale faults are less
        // valuable than recent telemetry for the diagnostics-bundle snapshot cap; the bus
        // already retains its own 50-item ring so late-visible replay is still possible.
        var channel = Channel.CreateBounded<PipelineTransientFault>(
            new BoundedChannelOptions(256)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true,
                SingleWriter = false,
            });

        var observer = new ChannelWriterObserver(channel.Writer);
        using IDisposable subscription = _transientFaultBus.Subscribe(observer);
        try
        {
            await foreach (PipelineTransientFault fault in
                channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                yield return fault;
            }
        }
        finally
        {
            channel.Writer.TryComplete();
        }
    }

    private sealed class ChannelWriterObserver : IObserver<PipelineTransientFault>
    {
        private readonly ChannelWriter<PipelineTransientFault> writer;

        public ChannelWriterObserver(ChannelWriter<PipelineTransientFault> writer)
        {
            this.writer = writer;
        }

        public void OnNext(PipelineTransientFault value) => writer.TryWrite(value);

        public void OnError(Exception error) => writer.TryComplete(error);

        public void OnCompleted() => writer.TryComplete();
    }
}
