using Trackdub.Application.Artifacts;
using Trackdub.Application.Logging;
using Trackdub.Contracts;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.LipSync;

/// <summary>Source timing for one translated segment used for source forced-alignment.</summary>
public sealed record SegmentSourceTiming(double StartSeconds, double EndSeconds);

public sealed record LipSyncStageRequest(
    Guid ProjectId,
    MediaAsset MediaAsset,
    IReadOnlyList<TtsTake> TtsTakes,
    IReadOnlyList<ProjectArtifact> ExistingArtifacts,
    bool IsEnabled = true,
    string? PreferredModelAlias = null,
    PhonemeStretchBounds? StretchBounds = null,
    /// <summary>
    /// Maps TranslatedSegmentId → TRANSLATED (target-language) text. Used to align the
    /// TTS take audio, which speaks the translated text.
    /// </summary>
    IReadOnlyDictionary<Guid, string>? SegmentTranscriptMap = null,
    /// <summary>
    /// Maps TranslatedSegmentId → source audio timing for forced-alignment of the original
    /// speech. When present the handler runs source alignment and attempts phoneme stretching.
    /// </summary>
    IReadOnlyDictionary<Guid, SegmentSourceTiming>? SegmentSourceTimingMap = null,
    /// <summary>
    /// Maps TranslatedSegmentId → ORIGINAL (source-language) transcript text. Used to align
    /// the source audio clip, which speaks the original language. Never reuse the translated
    /// map here: aligning source audio against translated text yields bogus phoneme timings.
    /// </summary>
    IReadOnlyDictionary<Guid, string>? SourceSegmentTranscriptMap = null,
    /// <summary>
    /// Absolute path to the best available source audio file (NormalizedAudio, enhanced, or vocals).
    /// Required when SegmentSourceTimingMap is provided.
    /// </summary>
    string? SourceAudioPath = null,
    /// <summary>
    /// BCP-47 source language for eSpeak phonemization of source-audio alignment.
    /// </summary>
    string? SourceLanguageCode = null,
    /// <summary>
    /// BCP-47 target language for eSpeak phonemization of TTS-take alignment.
    /// </summary>
    string? TargetLanguageCode = null);

public sealed record LipSyncStageResult(
    StageRunRecord StageRun,
    IReadOnlyList<LipSyncSegment> Segments);

public sealed class LipSyncStageHandler(
    IForcedAligner forcedAligner,
    IPhonemeTimingPlanner phonemeTimingPlanner,
    IPhonemeStretchService phonemeStretchService,
    IArtifactStore artifactStore,
    IProjectStageRunStore stageRunStore,
    PipelineDegradationWriter? degradationWriter = null,
    IRuntimePlanningPreferences? runtimePlanningPreferences = null,
    IApplicationLogger? logger = null,
    IFileFingerprintService? fileFingerprintService = null,
    IMediaAssetRepository? mediaAssetRepository = null,
    IAudioClipExtractor? audioClipExtractor = null)
{
    private readonly IPhonemeTimingPlanner _phonemeTimingPlanner = phonemeTimingPlanner;
    private readonly IPhonemeStretchService _phonemeStretchService = phonemeStretchService;
    private readonly IFileFingerprintService? _fileFingerprintService = fileFingerprintService;
    private readonly IMediaAssetRepository? _mediaAssetRepository = mediaAssetRepository;
    private readonly IAudioClipExtractor? _audioClipExtractor = audioClipExtractor;

    private static readonly PhonemeStretchBounds DefaultStretchBounds =
        new(MinRatio: 0.5, MaxRatio: 2.0, PreferredMaxVowelRatio: 1.5);

    public async Task<LipSyncStageResult> HandleAsync(
        LipSyncStageRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        StageRunRecord stageRun = await StageRunHelper
            .StartAsync(stageRunStore, request.ProjectId, StageNames.LipSync, cancellationToken)
            .ConfigureAwait(false);

        try
        {
            if (!request.IsEnabled)
            {
                const string skipReason = "Lip-sync stage is disabled for this run.";
                stageRun = await StageRunHelper
                    .SkipAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        skipReason, CancellationToken.None, runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);

                if (degradationWriter is not null)
                    await degradationWriter.WriteAsync(
                        new PipelineDegradationRecord(
                            Stage: StageNames.LipSync,
                            Code: "LipSyncDisabled",
                            Message: skipReason,
                            Detail: null,
                            SelectedFallback: "original-tts-take",
                            RecommendedAction: null,
                            OccurredAtUtc: DateTimeOffset.UtcNow,
                            StageRunId: stageRun.Id),
                        request.ProjectId, request.MediaAsset.Id, CancellationToken.None)
                    .ConfigureAwait(false);

                return new LipSyncStageResult(stageRun, []);
            }

            var bounds = request.StretchBounds ?? DefaultStretchBounds;
            var segments = new List<LipSyncSegment>();

            foreach (var take in request.TtsTakes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var segment = await ProcessTakeAsync(take, bounds, stageRun.Id, request, cancellationToken)
                    .ConfigureAwait(false);
                segments.Add(segment);
            }

            bool anyActuallyAligned = segments.Any(s => s.Status == LipSyncSegmentStatus.Aligned);
            bool anyPartial = segments.Any(s => s.Status == LipSyncSegmentStatus.Partial);
            bool anyFailed = segments.Any(s => s.Status == LipSyncSegmentStatus.Failed);
            bool allSkipped = segments.All(s =>
                s.Status is not (LipSyncSegmentStatus.Aligned or LipSyncSegmentStatus.Partial or LipSyncSegmentStatus.Failed));

            if (segments.Count == 0)
            {
                // No TTS takes were provided — lip-sync cannot run.
                stageRun = await StageRunHelper
                    .SkipAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        "No TTS takes provided; lip-sync prerequisite not met.", CancellationToken.None,
                        runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);
                return new LipSyncStageResult(stageRun, []);
            }
            else if (allSkipped)
            {
                stageRun = await StageRunHelper
                    .SkipAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        "All segments skipped during lip-sync alignment.", CancellationToken.None,
                        runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);
            }
            else if (anyActuallyAligned && anyFailed)
            {
                // Some segments fully aligned but others failed — report as partial.
                stageRun = await StageRunHelper
                    .PartiallyCompleteAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        $"Some segments aligned, but {segments.Count(s => s.Status == LipSyncSegmentStatus.Failed)} failed.",
                        CancellationToken.None, runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);
            }
            else if (anyActuallyAligned && !anyPartial && !anyFailed)
            {
                // All segments fully aligned — only then complete.
                stageRun = await StageRunHelper
                    .CompleteAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        CancellationToken.None, runtimePlanningPreferences)
                    .ConfigureAwait(false);
            }
            else if (anyFailed && !anyActuallyAligned && !anyPartial)
            {
                // Every take failed — record as failed, not partial.
                stageRun = await StageRunHelper
                    .FailAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        $"All {segments.Count} segments failed lip-sync.", CancellationToken.None,
                        runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);
            }
            else
            {
                // Mixed or partial outcomes — report as partial.
                int failedCount = segments.Count(s => s.Status == LipSyncSegmentStatus.Failed);
                int partialCount = segments.Count(s => s.Status == LipSyncSegmentStatus.Partial);
                stageRun = await StageRunHelper
                    .PartiallyCompleteAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                        $"{partialCount} partial, {failedCount} failed, {segments.Count(s => s.Status == LipSyncSegmentStatus.Aligned)} aligned.",
                        CancellationToken.None, runtimePlanningPreferences, logger)
                    .ConfigureAwait(false);
            }

            return new LipSyncStageResult(stageRun, segments);
        }
        catch (OperationCanceledException)
        {
            await StageRunHelper
                .CancelAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                    "LipSync canceled.", CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);
            throw;
        }
        catch (Exception ex)
        {
            await StageRunHelper
                .FailAsync(stageRunStore, stageRun, forcedAligner as IStageRuntimeExecutionReporter,
                    ex.Message, CancellationToken.None, runtimePlanningPreferences, logger)
                .ConfigureAwait(false);

            if (degradationWriter is not null)
                await degradationWriter.WriteAsync(
                    new PipelineDegradationRecord(
                        Stage: StageNames.LipSync,
                        Code: "LipSyncUnhandledFailure",
                        Message: "Unhandled exception in lip-sync stage.",
                        Detail: ex.Message,
                        SelectedFallback: "original-tts-take",
                        RecommendedAction: "Check aligner configuration and audio paths.",
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        StageRunId: stageRun.Id),
                    request.ProjectId, request.MediaAsset.Id, CancellationToken.None)
                .ConfigureAwait(false);

            throw;
        }
    }

    private async Task<LipSyncSegment> ProcessTakeAsync(
        TtsTake take,
        PhonemeStretchBounds bounds,
        Guid stageRunId,
        LipSyncStageRequest request,
        CancellationToken cancellationToken)
    {
        if (take.ArtifactId is null || take.TranslatedSegmentId is null)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId ?? take.Id,
                Status: LipSyncSegmentStatus.SkippedNoPhonemes,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: null,
                SkipReason: "TTS take has no artifact or segment reference.",
                FailureReason: null,
                ProviderId: null,
                ModelId: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        var ttsTakeArtifact = request.ExistingArtifacts
            .FirstOrDefault(a => a.Id == take.ArtifactId);

        if (ttsTakeArtifact is null)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.SkippedNoPhonemes,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: take.PreStretchDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
                    : TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: null,
                SkipReason: "TTS artifact not found in project artifacts list.",
                FailureReason: null,
                ProviderId: null,
                ModelId: null,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        var ttsTakeAudioPath = artifactStore.GetPath(ttsTakeArtifact.RelativePath);
        string transcript = request.SegmentTranscriptMap is not null
            && request.SegmentTranscriptMap.TryGetValue(take.TranslatedSegmentId.Value, out string? text)
            ? text
            : string.Empty;
        var alignmentRequest = new ForcedAlignmentRequest(
            AudioPath: ttsTakeAudioPath,
            NormalizedTranscript: transcript,
            LanguageCode: request.TargetLanguageCode,
            SegmentId: take.TranslatedSegmentId.Value.ToString(),
            Options: new ForcedAlignmentOptions(
                RequirePhonemeTimings: true,
                PreferredModelAlias: request.PreferredModelAlias));

        ForcedAlignmentResult alignmentResult = await forcedAligner
            .AlignAsync(alignmentRequest, cancellationToken)
            .ConfigureAwait(false);

        if (alignmentResult.Status == ForcedAlignmentStatus.Skipped)
        {
            if (degradationWriter is not null)
                await degradationWriter.WriteAsync(
                    new PipelineDegradationRecord(
                        Stage: StageNames.LipSync,
                        Code: "LipSyncAlignmentSkipped",
                        Message: alignmentResult.SkipReason ?? "Alignment skipped.",
                        Detail: $"SegmentId={take.TranslatedSegmentId}",
                        SelectedFallback: "original-tts-take",
                        RecommendedAction: null,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        StageRunId: stageRunId),
                    request.ProjectId, request.MediaAsset.Id, CancellationToken.None)
                .ConfigureAwait(false);

            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.SkippedLowConfidence,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: null,
                SkipReason: alignmentResult.SkipReason,
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        if (alignmentResult.Status == ForcedAlignmentStatus.Failed)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.Failed,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: null,
                SkipReason: null,
                FailureReason: "Aligner returned Failed status.",
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        if (alignmentResult.Phonemes.Count == 0)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.SkippedNoPhonemes,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: null,
                SkipReason: "Aligner returned zero phonemes.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        if (alignmentResult.Confidence.Overall < alignmentRequest.Options.MinOverallConfidence)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.SkippedLowConfidence,
                SourceAlignmentId: null,
                TtsAlignmentId: null,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: $"Overall confidence {alignmentResult.Confidence.Overall:F2} below threshold {alignmentRequest.Options.MinOverallConfidence}.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        // --- Source alignment + phoneme stretch path ---
        // Requires: source audio path, segment timing, and an audio clip extractor.
        // Without these inputs, stretching would produce a no-op (all ratios 1.0), which
        // violates stage-readiness semantics. Fall back to Partial with a clear reason.
        if (_audioClipExtractor is null
            || string.IsNullOrEmpty(request.SourceAudioPath)
            || request.SegmentSourceTimingMap is null
            || !request.SegmentSourceTimingMap.TryGetValue(take.TranslatedSegmentId.Value, out SegmentSourceTiming? sourceTiming))
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.Partial,
                SourceAlignmentId: null,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: take.PreStretchDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
                    : TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: "Source audio or timing not available; phoneme stretch skipped.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        // Source alignment must use the ORIGINAL (source-language) transcript: the source
        // audio speaks the original language, while SegmentTranscriptMap holds translated
        // text for the TTS take. Without the original text, skip the stretch path honestly.
        string sourceTranscript = request.SourceSegmentTranscriptMap is not null
            && request.SourceSegmentTranscriptMap.TryGetValue(take.TranslatedSegmentId.Value, out string? sourceText)
            ? sourceText
            : string.Empty;

        if (string.IsNullOrWhiteSpace(sourceTranscript))
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.Partial,
                SourceAlignmentId: null,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.FromSeconds(sourceTiming.EndSeconds - sourceTiming.StartSeconds),
                TtsDuration: take.PreStretchDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
                    : TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: "Source-language transcript not available; phoneme stretch skipped.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        // Extract source audio segment to a temporary file.
        string tempSourceSegmentPath = Path.Combine(Path.GetTempPath(),
            $"trackdub-src-seg-{take.TranslatedSegmentId.Value:N}.wav");
        try
        {
            await _audioClipExtractor
                .ExtractAsync(
                    request.SourceAudioPath,
                    sourceTiming.StartSeconds,
                    sourceTiming.EndSeconds,
                    tempSourceSegmentPath,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.Partial,
                SourceAlignmentId: null,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.Zero,
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: $"Source audio segment extraction failed: {ex.Message}",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        ForcedAlignmentResult sourceAlignmentResult;
        try
        {
            var sourceAlignmentRequest = new ForcedAlignmentRequest(
                AudioPath: tempSourceSegmentPath,
                NormalizedTranscript: sourceTranscript,
                LanguageCode: request.SourceLanguageCode,
                SegmentId: $"src-{take.TranslatedSegmentId.Value}",
                Options: new ForcedAlignmentOptions(
                    AllowPartial: true,
                    RequirePhonemeTimings: true,
                    PreferredModelAlias: request.PreferredModelAlias));

            sourceAlignmentResult = await forcedAligner
                .AlignAsync(sourceAlignmentRequest, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try { File.Delete(tempSourceSegmentPath); } catch { /* best-effort cleanup */ }
        }

        if (sourceAlignmentResult.Status is ForcedAlignmentStatus.Failed
            || sourceAlignmentResult.Phonemes.Count == 0)
        {
            // Source alignment failed or returned no phonemes — return what TTS alignment gave us.
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.Partial,
                SourceAlignmentId: sourceAlignmentResult.SegmentId,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.FromSeconds(sourceTiming.EndSeconds - sourceTiming.StartSeconds),
                TtsDuration: take.PreStretchDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
                    : TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: "Source alignment produced no phonemes; stretch skipped.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        // Plan per-phoneme stretch ratios from source vs. TTS phonemes.
        IReadOnlyList<PhonemeStretchPlan> stretchPlan =
            _phonemeTimingPlanner.PlanStretches(
                sourceAlignmentResult.Phonemes,
                alignmentResult.Phonemes,
                bounds);

        bool allOutOfBounds = stretchPlan.Count > 0 && stretchPlan.All(static p => !p.WithinBounds);
        if (allOutOfBounds)
        {
            if (degradationWriter is not null)
                await degradationWriter.WriteAsync(
                    new PipelineDegradationRecord(
                        Stage: StageNames.LipSync,
                        Code: "LipSyncUnsafeStretch",
                        Message: "All phoneme stretch ratios are out of safe bounds; original TTS take preserved.",
                        Detail: $"SegmentId={take.TranslatedSegmentId}",
                        SelectedFallback: "original-tts-take",
                        RecommendedAction: null,
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        StageRunId: stageRunId),
                    request.ProjectId, request.MediaAsset.Id, CancellationToken.None)
                .ConfigureAwait(false);

            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.SkippedUnsafeStretchRatio,
                SourceAlignmentId: sourceAlignmentResult.SegmentId,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.FromSeconds(sourceTiming.EndSeconds - sourceTiming.StartSeconds),
                TtsDuration: take.PreStretchDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
                    : TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: "All phoneme stretch ratios are outside safe bounds.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        // Apply WSOLA stretch.
        string stretchedOutputPath = Path.Combine(Path.GetTempPath(),
            $"trackdub-lip-stretched-{take.TranslatedSegmentId.Value:N}.wav");
        TimeSpan? alignedDuration;
        try
        {
            alignedDuration = await _phonemeStretchService
                .StretchAsync(
                    ttsTakeAudioPath,
                    stretchedOutputPath,
                    stretchPlan,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.Failed,
                SourceAlignmentId: sourceAlignmentResult.SegmentId,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.FromSeconds(sourceTiming.EndSeconds - sourceTiming.StartSeconds),
                TtsDuration: TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: null,
                FailureReason: $"Phoneme stretch failed: {ex.Message}",
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        if (alignedDuration is null)
        {
            // Stretch service skipped (e.g. all ratios out of bounds at service level).
            try { File.Delete(stretchedOutputPath); } catch { /* best-effort */ }
            return new LipSyncSegment(
                SegmentId: take.TranslatedSegmentId.Value,
                Status: LipSyncSegmentStatus.SkippedUnsafeStretchRatio,
                SourceAlignmentId: sourceAlignmentResult.SegmentId,
                TtsAlignmentId: alignmentResult.SegmentId,
                SourceDuration: TimeSpan.FromSeconds(sourceTiming.EndSeconds - sourceTiming.StartSeconds),
                TtsDuration: take.PreStretchDurationSeconds.HasValue
                    ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
                    : TimeSpan.Zero,
                AlignedTtsDuration: null,
                PlanConfidence: alignmentResult.Confidence.Overall,
                SkipReason: "Stretch service skipped: all phoneme ratios out of bounds.",
                FailureReason: null,
                ProviderId: alignmentResult.ProviderId,
                ModelId: alignmentResult.ModelId,
                CreatedAtUtc: DateTimeOffset.UtcNow);
        }

        // Persist the stretched output as a LipSyncTake artifact.
        bool anyOutOfBounds = stretchPlan.Any(static p => !p.WithinBounds);
        string relativeOutputPath = ProjectArtifactPaths.GetLipSyncTakeRelativePath(
            take.TranslatedSegmentId.Value, stageRunId);

        string absOutputPath;
        try
        {
            await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(relativeOutputPath));
            File.Copy(stretchedOutputPath, tx.TemporaryPath, overwrite: true);
            await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);
            absOutputPath = artifactStore.GetPath(relativeOutputPath);
        }
        finally
        {
            try { File.Delete(stretchedOutputPath); } catch { /* best-effort */ }
        }

        // Register the stretched output as a ProjectArtifact so the mix planner
        // can find it via BuildLipSyncByTakeId (provenance = "lipsync:take:{ttsTakeId:N}").
        if (_mediaAssetRepository is not null)
        {
            string sha256 = "unknown";
            long sizeBytes = 0L;
            if (_fileFingerprintService is not null)
            {
                FileFingerprint fp = await _fileFingerprintService
                    .ComputeAsync(absOutputPath, cancellationToken)
                    .ConfigureAwait(false);
                sha256 = fp.Sha256;
                sizeBytes = fp.SizeBytes;
            }

            var lipSyncArtifact = new ProjectArtifact(
                Id: Guid.NewGuid(),
                ProjectId: request.ProjectId,
                MediaAssetId: request.MediaAsset.Id,
                Kind: ArtifactKind.LipSyncTake,
                RelativePath: relativeOutputPath,
                Sha256: sha256,
                SizeBytes: sizeBytes,
                DurationSeconds: alignedDuration?.TotalSeconds,
                SampleRate: null,
                ChannelCount: null,
                CreatedAtUtc: DateTimeOffset.UtcNow,
                StageRunId: stageRunId,
                Provenance: $"lipsync:take:{take.Id:N}");

            await _mediaAssetRepository
                .SaveArtifactAsync(lipSyncArtifact, cancellationToken)
                .ConfigureAwait(false);
        }
        else
        {
            // File was written to disk above but metadata is not discoverable
            // by the mix planner — record a degradation so the pipeline can
            // fall back to the original TTS take instead of a ghost artifact.
            if (degradationWriter is not null)
                await degradationWriter.WriteAsync(
                    new PipelineDegradationRecord(
                        Stage: StageNames.LipSync,
                        Code: "LipSyncArtifactNotRegistered",
                        Message: "Lip-sync alignment succeeded but the LipSyncTake artifact metadata could not be registered in the project store; mix planner will not discover the aligned output.",
                        Detail: $"SegmentId={take.TranslatedSegmentId}, ArtifactPath={relativeOutputPath}",
                        SelectedFallback: "original-tts-take",
                        RecommendedAction: "Ensure IMediaAssetRepository is registered in the DI container.",
                        OccurredAtUtc: DateTimeOffset.UtcNow,
                        StageRunId: stageRunId),
                    request.ProjectId, request.MediaAsset.Id, CancellationToken.None)
                .ConfigureAwait(false);

            logger?.LogWarning(
                "Lip-sync alignment succeeded but LipSyncTake artifact metadata was not registered; IMediaAssetRepository unavailable. SegmentId={SegmentId}",
                take.TranslatedSegmentId);
        }

        TimeSpan sourceDuration = TimeSpan.FromSeconds(sourceTiming.EndSeconds - sourceTiming.StartSeconds);
        TimeSpan ttsDuration = take.PreStretchDurationSeconds.HasValue
            ? TimeSpan.FromSeconds(take.PreStretchDurationSeconds.Value)
            : TimeSpan.Zero;

        return new LipSyncSegment(
            SegmentId: take.TranslatedSegmentId.Value,
            Status: anyOutOfBounds ? LipSyncSegmentStatus.Partial : LipSyncSegmentStatus.Aligned,
            SourceAlignmentId: sourceAlignmentResult.SegmentId,
            TtsAlignmentId: alignmentResult.SegmentId,
            SourceDuration: sourceDuration,
            TtsDuration: ttsDuration,
            AlignedTtsDuration: alignedDuration,
            PlanConfidence: alignmentResult.Confidence.Overall,
            SkipReason: anyOutOfBounds ? "Some phoneme ratios were clamped to safe bounds." : null,
            FailureReason: null,
            ProviderId: alignmentResult.ProviderId,
            ModelId: alignmentResult.ModelId,
            CreatedAtUtc: DateTimeOffset.UtcNow);
    }
}
