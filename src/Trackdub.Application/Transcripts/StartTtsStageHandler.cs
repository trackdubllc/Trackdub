using System.Collections.Concurrent;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trackdub.Application.Artifacts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed record StartTtsStageRequest(
    Guid ProjectId,
    MediaAsset MediaAsset,
    Guid SpeakerId,
    string TargetLanguage,
    VoiceAssignment VoiceAssignment,
    IReadOnlyList<TranscriptSegment> TranscriptSegments,
    IReadOnlyList<TranslatedSegment> TranslatedSegments,
    IReadOnlySet<int>? SegmentIndices = null,
    string? PreferredModelAlias = null,
    IReadOnlyList<ProjectArtifact>? ProjectArtifacts = null,
    bool UseReferenceClipForVoiceCloning = false,
    ExecutionProviderKind? PreferredExecutionProvider = null,
    bool RequirePreferredExecutionProvider = false,
    string? PreferredModelVariantAlias = null);

public sealed record StartTtsStageResult(
    StageRunRecord StageRun,
    IReadOnlyList<TtsTake> Takes);

public sealed class StartTtsStageHandler(
    ITtsEngine ttsEngine,
    IVoiceCatalog voiceCatalog,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    ITtsTakeRepository ttsTakeRepository,
    IProjectStageRunStore stageRunStore,
    DurationAnalysisService? durationAnalysisService = null,
    IAudioTimeStretchService? audioTimeStretchService = null,
    TtsTimingOptions? timingOptions = null,
    ITtsAudioPostProcessor? ttsAudioPostProcessor = null,
    IConsentService? consentService = null,
    ISpeakerConsentService? speakerConsentService = null,
    IVoiceCloneAuditLog? voiceCloneAuditLog = null,
    IReferenceClipAnalyzer? referenceClipAnalyzer = null,
    IApplicationLogger? logger = null,
    PipelineDegradationWriter? degradationWriter = null,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null)
    : IDisposable
{
    private const string TtsAudioPostProcessVersion = "tts-audio-trim-v1";
    private const int TtsMaxConcurrency = 4;

    private readonly DurationAnalysisService durationAnalysisService = durationAnalysisService ?? new DurationAnalysisService();
    private readonly TtsTimingOptions timingOptions = (timingOptions ?? TtsTimingOptions.Default).Normalize();
    private readonly SemaphoreSlim persistenceGate = new(1, 1);

    public async Task<StartTtsStageResult> HandleAsync(
        StartTtsStageRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Create the stage run FIRST so that any prerequisite-resolution failure
        // (voice catalog lookup, reference clip analysis, consent check) is recorded
        // as a Failed StageRun rather than escaping unobserved.
        StageRunRecord stageRun = await StageRunHelper
            .StartAsync(stageRunStore, request.ProjectId, StageNames.Tts, cancellationToken)
            .ConfigureAwait(false);

        bool isVoiceCloning = request.UseReferenceClipForVoiceCloning &&
            request.VoiceAssignment.ReferenceClipArtifactId is not null;
        VoiceCatalogEntry voice;
        VoiceCloneReference? voiceCloneReference;
        try
        {
            voice = ResolveVoice(request, isVoiceCloning);

            // Resolve and validate the reference clip once for the entire batch so that the
            // audio analysis is not repeated for every synthesized segment.
            voiceCloneReference = isVoiceCloning
                ? await ResolveVoiceCloneReferenceAsync(request, cancellationToken).ConfigureAwait(false)
                : null;
        }
        catch (OperationCanceledException)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, stageRun, ttsEngine, "TTS canceled.", CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await StageRunHelper
                .FailAsync(stageRunStore, stageRun, ttsEngine, ex.Message, cancellationToken, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }

        var takes = new ConcurrentBag<TtsTake>();
        try
        {
            ConcurrentDictionary<string, byte> reservedArtifactRelativePaths
                = await BuildReservedArtifactRelativePathsAsync(request, cancellationToken).ConfigureAwait(false);
            Dictionary<int, TranscriptSegment> transcriptSegmentsByIndex = request.TranscriptSegments
                .Where(segment => segment.SpeakerId == request.SpeakerId)
                .ToDictionary(segment => segment.SegmentIndex);
            IEnumerable<TranslatedSegment> targetSegmentsQuery = request.TranslatedSegments
                .OrderBy(segment => segment.SegmentIndex)
                .Where(segment => transcriptSegmentsByIndex.ContainsKey(segment.SegmentIndex));

            if (request.SegmentIndices is { Count: > 0 } requestedIndices)
            {
                targetSegmentsQuery = targetSegmentsQuery.Where(segment => requestedIndices.Contains(segment.SegmentIndex));
            }

            // Materialize before the loop so we know whether any segments were actually
            // targeted for this speaker before deciding to fail on language incompatibility.
            TranslatedSegment[] targetSegments = targetSegmentsQuery.ToArray();
            bool voiceCompatible = IsVoiceLanguageCompatible(voice.LanguageCode, request.TargetLanguage);
            PipelineProgressReporter.Phase(
                progress,
                StageNames.Tts,
                "Preparing segments",
                $"{targetSegments.Length} segment(s) queued.");
            if (!voiceCompatible && targetSegments.Length > 0 && degradationWriter is not null)
            {
                try
                {
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            StageNames.Tts,
                            "TTS_LANGUAGE_UNSUPPORTED",
                            $"Voice '{voice.DisplayName}' supports '{voice.LanguageCode.Split('-')[0].ToLowerInvariant()}' base language; '{request.TargetLanguage}' segments skipped.",
                            Detail: null,
                            SelectedFallback: null,
                            RecommendedAction: "Assign a voice that supports the target language.",
                            DateTimeOffset.UtcNow,
                            stageRun.Id),
                        request.ProjectId,
                        request.MediaAsset.Id,
                        cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    // Degradation write is best-effort; failure must not abort the language-skip fallback.
                }
            }

            if (voiceCompatible)
            {
                var ctx = new SegmentProcessingContext(takes, targetSegments.Length);
                PipelineProgressReporter.Determinate(
                    progress,
                    StageNames.Tts,
                    0,
                    targetSegments.Length,
                    "Synthesizing segments",
                    $"{targetSegments.Length} segment(s) queued.");
                await Parallel.ForEachAsync(
                    targetSegments,
                    new ParallelOptions
                    {
                        MaxDegreeOfParallelism = TtsMaxConcurrency,
                        CancellationToken = cancellationToken
                    },
                    (translatedSegment, ct) => ProcessTranslatedSegmentAsync(
                        translatedSegment,
                        request,
                        stageRun.Id,
                        transcriptSegmentsByIndex,
                        reservedArtifactRelativePaths,
                        voice,
                        voiceCloneReference,
                        ctx,
                        progress,
                        ct)).ConfigureAwait(false);
            }

            // Only fail when segments were actually targeted but none synthesized due to
            // language mismatch. A speaker with no segments is a legitimate no-op.
            if (!voiceCompatible && targetSegments.Length > 0 && takes.IsEmpty)
            {
                throw new InvalidOperationException(
                    $"Voice '{voice.DisplayName}' does not support target language '{request.TargetLanguage}'. No audio was synthesized.");
            }

            stageRun = await StageRunHelper
                .CompleteAsync(stageRunStore, stageRun, ttsEngine, cancellationToken, runtimePlanningPreferences)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, stageRun, ttsEngine, "TTS canceled.", CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            if (!takes.IsEmpty)
            {
                await StageRunHelper
                    .PartiallyCompleteAsync(
                        stageRunStore,
                        stageRun,
                        ttsEngine,
                        $"TTS generated {takes.Count} take(s) before failing: {ex.Message}",
                        CancellationToken.None,
                        runtimePlanningPreferences,
                        logger)
                    .ConfigureAwait(false);
            }
            else
            {
                await StageRunHelper
                    .FailAsync(stageRunStore, stageRun, ttsEngine, ex.Message, cancellationToken, runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);
            }

            throw;
        }

        return new StartTtsStageResult(stageRun, [.. takes.OrderBy(static take => take.SegmentIndex)]);
    }

    /// <summary>Shared mutable state threaded through <see cref="ProcessTranslatedSegmentAsync"/>.</summary>
    private sealed class SegmentProcessingContext(ConcurrentBag<TtsTake> takes, int totalSegments)
    {
        public readonly ConcurrentBag<TtsTake> Takes = takes;
        public readonly int TotalSegments = totalSegments;
        public int CompletedSegments; // written exclusively via Interlocked
    }

    private async ValueTask ProcessTranslatedSegmentAsync(
        TranslatedSegment translatedSegment,
        StartTtsStageRequest request,
        Guid stageRunId,
        Dictionary<int, TranscriptSegment> transcriptSegmentsByIndex,
        ConcurrentDictionary<string, byte> reservedArtifactRelativePaths,
        VoiceCatalogEntry voice,
        VoiceCloneReference? voiceCloneReference,
        SegmentProcessingContext ctx,
        IProgress<PipelineProgressEvent>? progress,
        CancellationToken cancellationToken)
    {
        void ReportProgress(int completed, string detail) =>
            PipelineProgressReporter.Determinate(
                progress,
                StageNames.Tts,
                completed,
                ctx.TotalSegments,
                "Synthesizing segments",
                detail,
                currentItemLabel: $"Segment {translatedSegment.SegmentIndex}");

        TranscriptSegment sourceSegment = transcriptSegmentsByIndex[translatedSegment.SegmentIndex];
        TtsTake take;
        try
        {
            take = await SynthesizeSegmentAsync(
                request,
                stageRunId,
                translatedSegment,
                sourceSegment,
                voice,
                voiceCloneReference,
                reservedArtifactRelativePaths,
                cancellationToken).ConfigureAwait(false);
        }
        catch (TtsReferenceTextRequiredException)
        {
            await WriteMissingReferenceTextDegradationAsync(
                translatedSegment.SegmentIndex,
                request.ProjectId,
                request.MediaAsset.Id,
                stageRunId,
                cancellationToken).ConfigureAwait(false);

            int skipped = Interlocked.Increment(ref ctx.CompletedSegments);
            ReportProgress(skipped, $"Segment {skipped} of {ctx.TotalSegments} (skipped: missing reference text)");
            return;
        }

        ctx.Takes.Add(take);
        int completed = Interlocked.Increment(ref ctx.CompletedSegments);
        ReportProgress(completed, $"Segment {completed} of {ctx.TotalSegments}");
    }

    private async Task WriteMissingReferenceTextDegradationAsync(
        int segmentIndex,
        Guid projectId,
        Guid mediaAssetId,
        Guid stageRunId,
        CancellationToken cancellationToken)
    {
        if (degradationWriter is null)
        {
            return;
        }

        try
        {
            await degradationWriter.WriteAsync(
                new PipelineDegradationRecord(
                    StageNames.Tts,
                    "TTS_REFERENCE_TEXT_MISSING",
                    $"Segment {segmentIndex} skipped: Qwen3-TTS Base cloning requires source transcript text for the reference clip.",
                    Detail: null,
                    SelectedFallback: null,
                    RecommendedAction: "Transcribe the source segment or choose a preset Qwen3 CustomVoice model.",
                    DateTimeOffset.UtcNow,
                    stageRunId),
                projectId,
                mediaAssetId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Degradation write is best-effort; failure must not abort the skip fallback.
        }
    }

    public void Dispose() => persistenceGate.Dispose();

    private async Task<TtsTake> SynthesizeSegmentAsync(
        StartTtsStageRequest request,
        Guid stageRunId,
        TranslatedSegment translatedSegment,
        TranscriptSegment sourceSegment,
        VoiceCatalogEntry voice,
        VoiceCloneReference? voiceCloneReference,
        ConcurrentDictionary<string, byte> reservedArtifactRelativePaths,
        CancellationToken cancellationToken)
    {
        InferenceRequestOptions options = CreateTtsRequestOptions(request, voiceCloneReference is not null);
        string inputFingerprint = ComputeInputFingerprint(
            translatedSegment.Id,
            translatedSegment.Text,
            voice.VoiceId,
            request.TargetLanguage,
            voiceCloneReference?.ReferenceClipArtifactId,
            sourceSegment.EndSeconds - sourceSegment.StartSeconds,
            options.NormalizedPreferredModelAlias,
            options.NormalizedPreferredModelVariantAlias,
            timingOptions,
            request.VoiceAssignment.Id);
        TtsTake? cachedTake = await RunSerializedPersistenceAsync(
            ct => ttsTakeRepository.GetByFingerprintAsync(request.ProjectId, inputFingerprint, ct),
            cancellationToken)
            .ConfigureAwait(false);
        if (cachedTake is not null)
        {
            // Voice-cloning consent and audit must be enforced even for cache hits —
            // the policy gate and audit trail cannot be skipped because synthesis is skipped.
            // Both are gated behind persistenceGate so the consent check and its matching audit
            // entry are written atomically relative to other parallel synthesis tasks.
            if (voiceCloneReference is not null)
            {
                await RunSerializedPersistenceAsync(
                    async ct =>
                    {
                        await EnsureVoiceCloningConsentAsync(request.SpeakerId, request.ProjectId, ct).ConfigureAwait(false);
                        if (voiceCloneAuditLog is not null)
                        {
                            await voiceCloneAuditLog.AppendAsync(
                                new VoiceCloneAuditEntry(
                                    DateTimeOffset.UtcNow,
                                    consentService!.SessionId,
                                    voiceCloneReference.SpeakerId,
                                    voiceCloneReference.ReferenceClipArtifactId),
                                ct).ConfigureAwait(false);
                        }
                    },
                    cancellationToken).ConfigureAwait(false);
            }

            LogTtsTakeProvenance(
                translatedSegment.SegmentIndex,
                cachedTake,
                "TTS cache hit",
                options);
            return cachedTake;
        }

        IReadOnlyList<TtsTake> existingTakes = await RunSerializedPersistenceAsync(
            ct => ttsTakeRepository.GetBySegmentAsync(translatedSegment.Id, ct),
            cancellationToken)
            .ConfigureAwait(false);
        string relativePath = ReserveNextTtsTakeRelativePath(
            request.SpeakerId,
            translatedSegment.Id,
            existingTakes,
            reservedArtifactRelativePaths);

        if (voiceCloneReference is not null)
        {
            await RunSerializedPersistenceAsync(
                async ct =>
                {
                    await EnsureVoiceCloningConsentAsync(request.SpeakerId, request.ProjectId, ct).ConfigureAwait(false);
                    if (voiceCloneAuditLog is not null)
                    {
                        await voiceCloneAuditLog.AppendAsync(
                            new VoiceCloneAuditEntry(
                                DateTimeOffset.UtcNow,
                                consentService!.SessionId,
                                voiceCloneReference.SpeakerId,
                                voiceCloneReference.ReferenceClipArtifactId),
                            ct).ConfigureAwait(false);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        else if (request.VoiceAssignment.ReferenceClipArtifactId is null)
        {
            logger?.LogInformation(
                $"No reference clip assigned for speaker {request.SpeakerId:D}; using stock Kokoro TTS.");
        }

        VoiceCloneReference? effectiveCloneReference = voiceCloneReference;
        if (voiceCloneReference is not null &&
            (Qwen3TtsDefaults.IsBaseAlias(options.NormalizedPreferredModelAlias) ||
             CosyVoiceDefaults.IsCosyVoiceAlias(options.NormalizedPreferredModelAlias)))
        {
            string? referenceTranscript = sourceSegment.Text?.Trim();
            if (string.IsNullOrWhiteSpace(referenceTranscript))
            {
                throw new TtsReferenceTextRequiredException();
            }

            effectiveCloneReference = voiceCloneReference with { ReferenceTranscript = referenceTranscript };
        }

        TtsSynthesisResult result = await ttsEngine.SynthesizeAsync(
            new TtsSynthesisRequest(
                translatedSegment.Text,
                request.TargetLanguage,
                voice,
                Options: options,
                VoiceCloneReference: effectiveCloneReference,
                TargetDurationSeconds: sourceSegment.EndSeconds - sourceSegment.StartSeconds),
            cancellationToken).ConfigureAwait(false);

        double? rawDurationSeconds = result.SampleRate > 0
            ? (double)result.DurationSamples / result.SampleRate
            : null;
        DurationAnalysisResult analysis = durationAnalysisService.Analyze(sourceSegment, rawDurationSeconds, timingOptions);
        double? artifactDurationSeconds = rawDurationSeconds;
        int artifactDurationSamples = result.DurationSamples;
        int artifactSampleRate = result.SampleRate;
        double? preStretchDurationSeconds = null;
        double? stretchRatioApplied = null;
        TtsStretchMode stretchMode = TtsStretchMode.None;
        TtsStretchEngine stretchEngine = TtsStretchEngine.None;
        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(relativePath));
        Directory.CreateDirectory(Path.GetDirectoryName(tx.TemporaryPath)!);
        await File.WriteAllBytesAsync(tx.TemporaryPath, result.WavBytes, cancellationToken)
            .ConfigureAwait(false);
        TtsAudioPostProcessResult postProcessResult = await PostProcessTemporaryTakeAsync(
            tx.TemporaryPath,
            result,
            cancellationToken).ConfigureAwait(false);
        artifactDurationSamples = postProcessResult.DurationSamples;
        artifactSampleRate = postProcessResult.SampleRate > 0 ? postProcessResult.SampleRate : result.SampleRate;
        rawDurationSeconds = postProcessResult.DurationSeconds ??
                             (artifactSampleRate > 0
                                 ? (double)artifactDurationSamples / artifactSampleRate
                                 : null);
        artifactDurationSeconds = rawDurationSeconds;
        analysis = durationAnalysisService.Analyze(sourceSegment, rawDurationSeconds, timingOptions);

        if (analysis.AutoStretchEligible && analysis.TempoRatio is double tempoRatio)
        {
            if (audioTimeStretchService is null)
            {
                throw new InvalidOperationException("Audio time stretching is not configured.");
            }

            AudioTimeStretchResult stretchResult = await StretchTemporaryTakeAsync(
                tx.TemporaryPath,
                tempoRatio,
                cancellationToken).ConfigureAwait(false);
            preStretchDurationSeconds = rawDurationSeconds;
            stretchRatioApplied = tempoRatio;
            stretchMode = TtsStretchMode.Automatic;
            stretchEngine = stretchResult.Engine;
            artifactDurationSeconds = analysis.OriginalDurationSeconds;
            if (artifactSampleRate > 0)
            {
                artifactDurationSamples = Math.Max(1, (int)Math.Round(analysis.OriginalDurationSeconds * artifactSampleRate));
            }
        }

        await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);

        string finalPath = artifactStore.GetPath(relativePath);
        FileFingerprint fingerprint = await fileFingerprintService
            .ComputeAsync(finalPath, cancellationToken)
            .ConfigureAwait(false);

        var artifact = new ProjectArtifact(
            Guid.NewGuid(),
            request.ProjectId,
            request.MediaAsset.Id,
            ArtifactKind.TtsTake,
            relativePath,
            fingerprint.Sha256,
            fingerprint.SizeBytes,
            artifactDurationSeconds,
            artifactSampleRate,
            ChannelCount: 1,
            DateTimeOffset.UtcNow,
            stageRunId,
            $"tts:{result.ModelId}:{result.VoiceId}");
        string translatedTextHash = TtsTextHash.Compute(translatedSegment.SegmentIndex, translatedSegment.Text);
        TtsTake take = (voiceCloneReference is null
                ? TtsTake.CreateStock(
                    request.ProjectId,
                    request.VoiceAssignment.Id,
                    translatedSegment.Id,
                    translatedSegment.SegmentIndex,
                    translatedTextHash,
                    inputFingerprint)
                : TtsTake.CreateVoiceCloned(
                    request.ProjectId,
                    request.VoiceAssignment.Id,
                    voiceCloneReference.ReferenceClipArtifactId,
                    translatedSegment.Id,
                    translatedSegment.SegmentIndex,
                    translatedTextHash,
                    inputFingerprint))
            .Complete(
                artifact.Id,
                stageRunId,
                artifactDurationSamples,
                artifactSampleRate,
                result.Provider,
                result.ModelId,
                result.VoiceId,
                analysis.OverrunRatio,
                preStretchDurationSeconds,
                stretchRatioApplied,
                stretchMode,
                stretchEngine);
        await RunSerializedPersistenceAsync(
            async ct =>
            {
                await mediaAssetRepository.SaveArtifactAsync(artifact, ct).ConfigureAwait(false);
                await ttsTakeRepository.SaveAsync(take, ct).ConfigureAwait(false);
            },
            cancellationToken).ConfigureAwait(false);
        LogTtsTakeProvenance(
            translatedSegment.SegmentIndex,
            take,
            "TTS synthesized",
            options);
        return take;
    }

    private void LogTtsTakeProvenance(
        int segmentIndex,
        TtsTake take,
        string source,
        InferenceRequestOptions options)
    {
        logger?.LogInformation(
            $"{source}: {PipelineRuntimeProvenanceFormatter.FormatTtsSegmentLogLine(
                segmentIndex,
                take.Provider,
                options.NormalizedPreferredModelAlias,
                take.ModelId,
                options.NormalizedPreferredModelVariantAlias,
                take.VoiceId)}");
    }

    private async Task<T> RunSerializedPersistenceAsync<T>(
        Func<CancellationToken, Task<T>> action,
        CancellationToken cancellationToken)
    {
        await persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            persistenceGate.Release();
        }
    }

    private async Task RunSerializedPersistenceAsync(
        Func<CancellationToken, Task> action,
        CancellationToken cancellationToken)
    {
        await persistenceGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await action(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            persistenceGate.Release();
        }
    }

    private async Task<TtsAudioPostProcessResult> PostProcessTemporaryTakeAsync(
        string temporaryPath,
        TtsSynthesisResult result,
        CancellationToken cancellationToken)
    {
        if (ttsAudioPostProcessor is null ||
            result.SampleRate <= 0 ||
            result.DurationSamples < 0)
        {
            return new TtsAudioPostProcessResult(result.DurationSamples, result.SampleRate);
        }

        TtsAudioPostProcessResult postProcessResult = await ttsAudioPostProcessor
            .ProcessAsync(
                new TtsAudioPostProcessRequest(
                    temporaryPath,
                    result.SampleRate,
                    result.DurationSamples),
                cancellationToken)
            .ConfigureAwait(false);
        return postProcessResult.DurationSamples >= 0 && postProcessResult.SampleRate > 0
            ? postProcessResult
            : new TtsAudioPostProcessResult(result.DurationSamples, result.SampleRate);
    }

    private async Task<ConcurrentDictionary<string, byte>> BuildReservedArtifactRelativePathsAsync(
        StartTtsStageRequest request,
        CancellationToken cancellationToken)
    {
        var reservedPaths = new ConcurrentDictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        foreach (ProjectArtifact artifact in request.ProjectArtifacts ?? [])
        {
            reservedPaths.TryAdd(artifact.RelativePath, 0);
        }

        IReadOnlyList<ProjectArtifact> currentArtifacts = await mediaAssetRepository
            .GetArtifactsAsync(request.ProjectId, cancellationToken)
            .ConfigureAwait(false);
        foreach (ProjectArtifact artifact in currentArtifacts)
        {
            reservedPaths.TryAdd(artifact.RelativePath, 0);
        }

        return reservedPaths;
    }

    private static string ReserveNextTtsTakeRelativePath(
        Guid speakerId,
        Guid translatedSegmentId,
        IReadOnlyList<TtsTake> existingTakes,
        ConcurrentDictionary<string, byte> reservedArtifactRelativePaths)
    {
        int takeNumber = existingTakes.Count + 1;
        while (true)
        {
            string relativePath = ProjectArtifactPaths.GetTtsTakeRelativePath(
                speakerId,
                translatedSegmentId,
                takeNumber);
            if (reservedArtifactRelativePaths.TryAdd(relativePath, 0))
            {
                return relativePath;
            }

            takeNumber++;
        }
    }

    private async Task<AudioTimeStretchResult> StretchTemporaryTakeAsync(
        string temporaryPath,
        double tempoRatio,
        CancellationToken cancellationToken)
    {
        string stretchedPath = $"{temporaryPath}.stretch.wav";
        try
        {
            AudioTimeStretchResult result = await audioTimeStretchService!.StretchAsync(
                new AudioTimeStretchRequest(
                    temporaryPath,
                    stretchedPath,
                    tempoRatio,
                    timingOptions.EnableRubberbandStretch,
                    timingOptions.RubberbandStretchThreshold),
                cancellationToken).ConfigureAwait(false);
            File.Move(stretchedPath, temporaryPath, overwrite: true);
            return result;
        }
        finally
        {
            if (File.Exists(stretchedPath))
            {
                File.Delete(stretchedPath);
            }
        }
    }

    private VoiceCatalogEntry ResolveVoice(StartTtsStageRequest request, bool isVoiceCloning)
    {
        if (isVoiceCloning)
        {
            return new VoiceCatalogEntry(
                $"voice-clone:{request.SpeakerId:D}",
                request.TargetLanguage,
                "synthetic",
                "Voice clone");
        }

        if (ShouldForceStockTtsAlias(request.PreferredModelAlias) &&
            IsNonEnglishSpanishLanguage(request.TargetLanguage))
        {
            return new VoiceCatalogEntry(
                "qwen3:ryan",
                request.TargetLanguage,
                "synthetic",
                "Ryan");
        }

        string voiceId = ResolveVoiceId(request.VoiceAssignment);
        if (voiceCatalog.TryGetVoice(voiceId, out VoiceCatalogEntry? voice))
        {
            return voice;
        }

        throw new InvalidOperationException($"Voicepack '{voiceId}' is not available.");
    }

    private async Task<VoiceCloneReference?> ResolveVoiceCloneReferenceAsync(
        StartTtsStageRequest request,
        CancellationToken cancellationToken)
    {
        if (request.VoiceAssignment.ReferenceClipArtifactId is not Guid referenceClipArtifactId)
        {
            return null;
        }

        ProjectArtifact referenceArtifact = (request.ProjectArtifacts ?? [])
            .FirstOrDefault(artifact => artifact.Id == referenceClipArtifactId && artifact.Kind == ArtifactKind.ReferenceClip)
            ?? throw new InvalidOperationException("The selected reference clip artifact was not found.");
        string referenceClipPath = artifactStore.GetPath(referenceArtifact.RelativePath);
        if (!File.Exists(referenceClipPath))
        {
            throw new FileNotFoundException("The selected reference clip file was not found.", referenceClipPath);
        }

        if (referenceClipAnalyzer is null)
        {
            throw new InvalidOperationException("Reference clip active-speech validation is not configured.");
        }

        ReferenceClipAnalysis analysis = await referenceClipAnalyzer
            .AnalyzeAsync(referenceClipPath, cancellationToken)
            .ConfigureAwait(false);
        if (analysis.ActiveSpeechSeconds < ReferenceClipPolicy.MinimumActiveSpeechSeconds)
        {
            throw new InvalidOperationException(
                $"Reference clip needs at least {ReferenceClipPolicy.MinimumActiveSpeechSeconds:F1} seconds of active speech; detected {analysis.ActiveSpeechSeconds:F2} seconds.");
        }

        if (analysis.HasRecommendedMaximumWarning)
        {
            logger?.LogWarning(
                $"Reference clip for speaker {request.SpeakerId:D} has {analysis.ActiveSpeechSeconds:F2}s of active speech; F5/Chatterbox voice cloning is best with 3-10s.");
        }

        return new VoiceCloneReference(
            request.SpeakerId,
            referenceClipArtifactId,
            referenceClipPath,
            analysis.TotalDurationSeconds,
            analysis.ActiveSpeechSeconds);
    }

    private async Task EnsureVoiceCloningConsentAsync(
        Guid speakerId,
        Guid projectId,
        CancellationToken cancellationToken)
    {
        if (speakerConsentService is not null)
        {
            if (await speakerConsentService.IsConsentGrantedAsync(speakerId, cancellationToken).ConfigureAwait(false))
            {
                consentService?.GrantVoiceCloningConsent();
                return;
            }

            if (consentService?.IsVoiceCloningConsentGranted == true)
            {
                await speakerConsentService.RecordConsentAsync(
                    projectId,
                    speakerId,
                    isThirdPartyConsent: false,
                    notes: null,
                    cancellationToken).ConfigureAwait(false);
                return;
            }
        }
        else if (consentService?.IsVoiceCloningConsentGranted == true)
        {
            return;
        }

        throw new ConsentRequiredException();
    }

    private static InferenceRequestOptions CreateTtsRequestOptions(
        StartTtsStageRequest request,
        bool isVoiceCloning)
    {
        if (isVoiceCloning)
        {
            return new InferenceRequestOptions(
                string.IsNullOrWhiteSpace(request.PreferredModelAlias)
                    ? VoiceCloningDefaults.ResolveDefaultChatterboxAlias(request.TargetLanguage)
                    : request.PreferredModelAlias.Trim(),
                RequirePreferredModelAlias: true,
                PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
                RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                PreferredModelVariantAlias: request.PreferredModelVariantAlias);
        }

        string? trimmedAlias = request.PreferredModelAlias?.Trim();
        bool isCosyVoiceAlias = TtsModelOverrideSettings.IsCosyVoiceAlias(trimmedAlias);
        bool isQwen3Alias = Qwen3TtsDefaults.IsAnyQwen3Alias(trimmedAlias);
        bool shouldForceStockTtsAlias = ShouldForceStockTtsAlias(trimmedAlias);
        bool shouldRequireExplicitAlias =
            shouldForceStockTtsAlias ||
            isCosyVoiceAlias ||
            isQwen3Alias;
        // Forced-stock path has no reference/speaker audio (isVoiceCloning is false here),
        // so it must stay on a stock text-only model. CosyVoice is a voice-cloning model
        // that requires a reference clip (see CosyVoiceTtsEngine), so it cannot serve this
        // path; routing a non-en/es language to it would fail at inference.
        string? preferredAlias = shouldForceStockTtsAlias
            ? IsNonEnglishSpanishLanguage(request.TargetLanguage)
                ? Qwen3TtsDefaults.ResolveCustomVoiceAlias(tier: null)
                : StockTtsDefaults.KokoroPrimaryAlias
            : trimmedAlias;
        return new InferenceRequestOptions(
            preferredAlias,
            RequirePreferredModelAlias: shouldRequireExplicitAlias,
            PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
            RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
            PreferredModelVariantAlias: request.PreferredModelVariantAlias);
    }

    private static bool IsVoiceCloningAlias(string? alias) =>
        NormalizeAlias(alias) is string normalizedAlias &&
        (normalizedAlias.Equals(VoiceCloningDefaults.ChatterboxPrimaryAlias, StringComparison.OrdinalIgnoreCase) ||
         normalizedAlias.Equals(VoiceCloningDefaults.ChatterboxFallbackAlias, StringComparison.OrdinalIgnoreCase) ||
         normalizedAlias.Equals(VoiceCloningDefaults.ChatterboxMultilingualAlias, StringComparison.OrdinalIgnoreCase) ||
         normalizedAlias.Equals(VoiceCloningDefaults.CosyVoicePrimaryAlias, StringComparison.OrdinalIgnoreCase) ||
         normalizedAlias.Equals(VoiceCloningDefaults.CosyVoiceFallbackAlias, StringComparison.OrdinalIgnoreCase) ||
         Qwen3TtsDefaults.IsBaseAlias(normalizedAlias) ||
         IsF5VoiceCloningAlias(normalizedAlias));

    private static bool ShouldForceStockTtsAlias(string? alias)
    {
        string? trimmedAlias = alias?.Trim();
        return string.IsNullOrWhiteSpace(trimmedAlias) ||
               (IsVoiceCloningAlias(trimmedAlias) &&
                !TtsModelOverrideSettings.IsCosyVoiceAlias(trimmedAlias) &&
                !Qwen3TtsDefaults.IsAnyQwen3Alias(trimmedAlias));
    }

    /// <summary>
    /// True when target language needs CosyVoice (non-en/es) — Kokoro only supports en/es.
    /// </summary>
    private static bool IsNonEnglishSpanishLanguage(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode)) return false;
        string lang = languageCode.Trim().Split('-')[0].ToLowerInvariant();
        return lang != "en" && lang != "es";
    }

    private static bool IsF5VoiceCloningAlias(string alias) =>
        alias.Equals("f5", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("f5tts", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("f5tts-onnx", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("f5-tts", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("f5-tts-onnx", StringComparison.OrdinalIgnoreCase) ||
        alias.Equals("swivid-f5-tts", StringComparison.OrdinalIgnoreCase);

    private static string? NormalizeAlias(string? alias) =>
        string.IsNullOrWhiteSpace(alias)
            ? null
            : alias.Trim();

    private static string ResolveVoiceId(VoiceAssignment assignment) =>
        string.IsNullOrWhiteSpace(assignment.VoiceVariant)
            ? assignment.VoiceModelId
            : assignment.VoiceVariant;

    public static bool HasDurationWarning(TtsTake take) =>
        take.DurationOverrunRatio is > 0.10d;

    private static bool IsVoiceLanguageCompatible(string voiceLanguageCode, string targetLanguage)
    {
        string trimmedVoiceCode = voiceLanguageCode.Trim();
        if (string.IsNullOrEmpty(trimmedVoiceCode) ||
            trimmedVoiceCode.Equals("mul", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        static string Normalize(string lang) => lang.Trim().Split('-')[0].ToLowerInvariant();
        return Normalize(trimmedVoiceCode) == Normalize(targetLanguage);
    }

    private static string ComputeInputFingerprint(
        Guid segmentId,
        string text,
        string voiceId,
        string targetLanguage,
        Guid? referenceClipArtifactId,
        double targetDurationSeconds,
        string? modelAlias,
        string? modelVariantAlias,
        TtsTimingOptions timingOptions,
        Guid voiceAssignmentId)
    {
        string input = string.Concat(
            segmentId.ToString("D"), "\0",
            text, "\0",
            voiceId, "\0",
            targetLanguage, "\0",
            referenceClipArtifactId?.ToString("D") ?? string.Empty, "\0",
            targetDurationSeconds.ToString("G17", CultureInfo.InvariantCulture), "\0",
            modelAlias ?? string.Empty, "\0",
            modelVariantAlias ?? string.Empty, "\0",
            timingOptions.AutoStretchMaxOverrun.ToString("G17", CultureInfo.InvariantCulture), "\0",
            timingOptions.MinimumStretchableDurationSeconds.ToString("G17", CultureInfo.InvariantCulture), "\0",
            timingOptions.EnableRubberbandStretch ? "1" : "0", "\0",
            timingOptions.RubberbandStretchThreshold.ToString("G17", CultureInfo.InvariantCulture), "\0",
            TtsAudioPostProcessVersion, "\0",
            voiceAssignmentId.ToString("D"));
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

}
