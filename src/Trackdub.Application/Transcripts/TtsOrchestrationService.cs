using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Trackdub.Application.Artifacts;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class TtsOrchestrationService(
    StartTtsStageHandler startTtsStageHandler,
    IVoiceAssignmentRepository voiceAssignmentRepository,
    ITtsTakeRepository ttsTakeRepository,
    ITtsEngine ttsEngine,
    IVoiceCatalog voiceCatalog,
    IArtifactStore artifactStore,
    IFileFingerprintService fileFingerprintService,
    IMediaAssetRepository mediaAssetRepository,
    IReferenceClipTrimmer referenceClipTrimmer,
    DurationAnalysisService? durationAnalysisService = null,
    IAudioTimeStretchService? audioTimeStretchService = null,
    TtsTimingOptions? timingOptions = null,
    IAudioClipExtractor? audioClipExtractor = null,
    IReferenceClipAnalyzer? referenceClipAnalyzer = null)
{
    private readonly StartTtsStageHandler startTtsStageHandler = startTtsStageHandler ?? throw new ArgumentNullException(nameof(startTtsStageHandler));
    private readonly IVoiceAssignmentRepository voiceAssignmentRepository = voiceAssignmentRepository ?? throw new ArgumentNullException(nameof(voiceAssignmentRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly ITtsEngine ttsEngine = ttsEngine ?? throw new ArgumentNullException(nameof(ttsEngine));
    private readonly IVoiceCatalog voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly IFileFingerprintService fileFingerprintService = fileFingerprintService ?? throw new ArgumentNullException(nameof(fileFingerprintService));
    private readonly IMediaAssetRepository mediaAssetRepository = mediaAssetRepository ?? throw new ArgumentNullException(nameof(mediaAssetRepository));
    private readonly DurationAnalysisService durationAnalysisService = durationAnalysisService ?? new DurationAnalysisService();
    private readonly TtsTimingOptions timingOptions = (timingOptions ?? TtsTimingOptions.Default).Normalize();
    private readonly IReferenceClipTrimmer referenceClipTrimmer = referenceClipTrimmer ?? throw new ArgumentNullException(nameof(referenceClipTrimmer));

    public Task GenerateTtsForSpeakerAsync(
        TranscriptProjectState currentState,
        GenerateTtsForSpeakerRequest request,
        CancellationToken cancellationToken) =>
        RunTtsForSpeakerAsync(
            currentState,
            request.SpeakerId,
            segmentIndices: null,
            voiceAssignmentOverride: null,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloning,
            request.PreferredExecutionProvider,
            request.RequirePreferredExecutionProvider,
            request.PreferredModelVariantAlias,
            cancellationToken);

    public async Task GenerateTtsForSegmentAsync(
        TranscriptProjectState currentState,
        GenerateTtsForSegmentRequest request,
        CancellationToken cancellationToken)
    {
        TranscriptRevision currentRevision = TranscriptWorkflowUtilities.GetRequiredTranscriptRevision(currentState);
        TranscriptWorkflowUtilities.EnsureRevisionMatches(
            currentRevision,
            request.TranscriptRevisionId,
            "Segment TTS was based on an out-of-date transcript revision.");

        TranscriptSegment segment = currentState.TranscriptSegments.FirstOrDefault(candidate => candidate.Id == request.SegmentId)
            ?? throw new InvalidOperationException("The selected segment was not found in the current transcript revision.");
        Guid speakerId = segment.SpeakerId
            ?? throw new InvalidOperationException("Assign the segment to a speaker before generating TTS.");

        await RunTtsForSpeakerAsync(
            currentState,
            speakerId,
            new HashSet<int> { segment.SegmentIndex },
            voiceAssignmentOverride: null,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloning,
            request.PreferredExecutionProvider,
            request.RequirePreferredExecutionProvider,
            request.PreferredModelVariantAlias,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task GenerateTtsForAllSpeakersAsync(
        TranscriptProjectState currentState,
        GenerateTtsForAllSpeakersRequest request,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        DateTimeOffset progressStartedAt = DateTimeOffset.UtcNow;
        PipelineProgressReporter.Started(progress, StageNames.Tts, phase: "Preparing speakers");
        try
        {
            Dictionary<Guid, VoiceAssignment> assignmentsBySpeakerId = currentState.VoiceAssignments
                .Where(assignment => !assignment.IsFallback)
                .ToDictionary(assignment => assignment.SpeakerId);
            ProjectSpeaker[] speakers = currentState.Speakers
                .OrderBy(speaker => speaker.CreatedAtUtc)
                .ToArray();

            for (int index = 0; index < speakers.Length; index++)
            {
                ProjectSpeaker speaker = speakers[index];
                string speakerLabel = BuildSpeakerProgressLabel(speaker, index + 1, speakers.Length);
                PipelineProgressReporter.Determinate(
                    progress,
                    StageNames.Tts,
                    index,
                    speakers.Length,
                    "Preparing speaker",
                    speakerLabel,
                    currentItemLabel: speaker.DisplayName);
                assignmentsBySpeakerId.TryGetValue(speaker.Id, out VoiceAssignment? assignment);
                VoiceAssignment? fallbackAssignment = null;
                if (assignment is null &&
                    request.FallbackVoiceIdsBySpeakerId is not null &&
                    request.FallbackVoiceIdsBySpeakerId.TryGetValue(speaker.Id, out string? fallbackVoiceId) &&
                    !string.IsNullOrWhiteSpace(fallbackVoiceId))
                {
                    fallbackAssignment = VoiceAssignment.CreateFallback(
                        currentState.ProjectState.Project.Id,
                        speaker.Id,
                        "kokoro-onnx",
                        fallbackVoiceId);
                    await voiceAssignmentRepository.SaveAsync(fallbackAssignment, cancellationToken).ConfigureAwait(false);
                }

                await RunTtsForSpeakerAsync(
                    currentState,
                    speaker.Id,
                    segmentIndices: null,
                    assignment ?? fallbackAssignment,
                    request.PreferredModelAlias,
                    request.UseReferenceClipForVoiceCloningBySpeakerId?.TryGetValue(speaker.Id, out bool useReferenceClipForVoiceCloning) == true
                        ? useReferenceClipForVoiceCloning
                        : false,
                    request.PreferredExecutionProvider,
                    request.RequirePreferredExecutionProvider,
                    request.PreferredModelVariantAlias,
                    cancellationToken,
                    progress).ConfigureAwait(false);
            }

            PipelineProgressReporter.Completed(
                progress,
                StageNames.Tts,
                DateTimeOffset.UtcNow - progressStartedAt,
                "TTS finished.");
        }
        catch (OperationCanceledException)
        {
            PipelineProgressReporter.Failed(
                progress,
                StageNames.Tts,
                "TTS canceled.",
                DateTimeOffset.UtcNow - progressStartedAt);
            throw;
        }
        catch (Exception ex)
        {
            PipelineProgressReporter.Failed(
                progress,
                StageNames.Tts,
                ex.Message,
                DateTimeOffset.UtcNow - progressStartedAt);
            throw;
        }
    }

    public async Task<PreviewVoiceResult> PreviewVoiceAsync(
        PreviewVoiceRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (string.IsNullOrWhiteSpace(request.SampleText))
        {
            throw new InvalidOperationException("Voice preview text is required.");
        }

        if (!voiceCatalog.TryGetVoice(request.VoiceId, out VoiceCatalogEntry? voice))
        {
            throw new InvalidOperationException($"Voicepack '{request.VoiceId}' is not available.");
        }

        string languageCode = string.IsNullOrWhiteSpace(request.LanguageCode)
            ? voice.LanguageCode
            : request.LanguageCode.Trim();
        TtsSynthesisResult result = await ttsEngine.SynthesizeAsync(
            new TtsSynthesisRequest(
                request.SampleText.Trim(),
                languageCode,
                voice,
                Options: new InferenceRequestOptions(
                    string.IsNullOrWhiteSpace(request.PreferredModelAlias)
                        ? StockTtsDefaults.KokoroPrimaryAlias
                        : request.PreferredModelAlias.Trim(),
                    RequirePreferredModelAlias: true,
                    PreferredExecutionProvider: request.PreferredExecutionProvider?.ToString(),
                    RequirePreferredExecutionProvider: request.RequirePreferredExecutionProvider,
                    PreferredModelVariantAlias: request.PreferredModelVariantAlias)),
            cancellationToken).ConfigureAwait(false);

        return new PreviewVoiceResult(
            result.WavBytes,
            result.SampleRate,
            result.ModelId,
            result.VoiceId,
            result.Provider);
    }

    public async Task RegenerateStaleTtsForSpeakerAsync(
        TranscriptProjectState currentState,
        RegenerateStaleTtsForSpeakerRequest request,
        CancellationToken cancellationToken)
    {
        HashSet<int> speakerSegmentIndices = currentState.TranscriptSegments
            .Where(segment => segment.SpeakerId == request.SpeakerId)
            .Select(segment => segment.SegmentIndex)
            .ToHashSet();
        HashSet<int> staleSegmentIndices = currentState.TtsSegmentStates
            .Where(state => state.IsStale && speakerSegmentIndices.Contains(state.SegmentIndex))
            .Select(state => state.SegmentIndex)
            .ToHashSet();
        if (staleSegmentIndices.Count == 0)
        {
            return;
        }

        await RunTtsForSpeakerAsync(
            currentState,
            request.SpeakerId,
            staleSegmentIndices,
            voiceAssignmentOverride: null,
            request.PreferredModelAlias,
            request.UseReferenceClipForVoiceCloning,
            request.PreferredExecutionProvider,
            request.RequirePreferredExecutionProvider,
            request.PreferredModelVariantAlias,
            cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task StretchTtsTakeAsync(
        TranscriptProjectState currentState,
        StretchTtsTakeRequest request,
        CancellationToken cancellationToken)
    {
        if (audioTimeStretchService is null)
        {
            throw new InvalidOperationException("Audio time stretching is not configured.");
        }

        TtsTake take = currentState.TtsTakes.FirstOrDefault(candidate => candidate.Id == request.TakeId)
            ?? throw new InvalidOperationException("The selected TTS take was not found.");
        if (take.Status is not TtsTakeStatus.Completed || take.IsStale)
        {
            throw new InvalidOperationException("Only a fresh completed TTS take can be stretched.");
        }

        TranscriptSegment sourceSegment = currentState.TranscriptSegments
            .FirstOrDefault(segment => segment.SegmentIndex == take.SegmentIndex)
            ?? throw new InvalidOperationException("The source segment for this TTS take was not found.");
        ProjectArtifact artifact = take.ArtifactId is Guid artifactId
            ? currentState.ProjectState.Artifacts.FirstOrDefault(candidate => candidate.Id == artifactId)
                ?? throw new InvalidOperationException("The TTS artifact for this take was not found.")
            : throw new InvalidOperationException("The TTS take does not reference an artifact.");
        int sampleRate = take.SampleRate ?? artifact.SampleRate ?? throw new InvalidOperationException("The TTS take sample rate is missing.");
        double? currentDurationSeconds = artifact.DurationSeconds ??
                                         (take.DurationSamples is int durationSamples && sampleRate > 0
                                             ? (double)durationSamples / sampleRate
                                             : null);
        double preStretchDurationSeconds = currentDurationSeconds
            ?? throw new InvalidOperationException("The TTS take duration is missing.");
        DurationAnalysisResult analysis = durationAnalysisService.Analyze(
            sourceSegment,
            currentDurationSeconds,
            timingOptions);
        if (!analysis.IsStretchable || analysis.TempoRatio is not double tempoRatio)
        {
            throw new InvalidOperationException("This TTS take is too short or too extreme to stretch cleanly.");
        }

        if (Math.Abs(tempoRatio - 1d) < 0.001d)
        {
            throw new InvalidOperationException("This TTS take already matches the source segment duration.");
        }

        string finalPath = artifactStore.GetPath(artifact.RelativePath);
        if (!File.Exists(finalPath))
        {
            throw new InvalidOperationException("The TTS artifact file is missing.");
        }

        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(artifact.RelativePath));
        AudioTimeStretchResult stretchResult = await audioTimeStretchService.StretchAsync(
            new AudioTimeStretchRequest(
                finalPath,
                tx.TemporaryPath,
                tempoRatio,
                timingOptions.EnableRubberbandStretch,
                timingOptions.RubberbandStretchThreshold),
            cancellationToken).ConfigureAwait(false);
        await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);

        FileFingerprint fingerprint = await fileFingerprintService
            .ComputeAsync(finalPath, cancellationToken)
            .ConfigureAwait(false);
        ProjectArtifact updatedArtifact = artifact with
        {
            Sha256 = fingerprint.Sha256,
            SizeBytes = fingerprint.SizeBytes,
            DurationSeconds = analysis.OriginalDurationSeconds,
            SampleRate = sampleRate,
            ChannelCount = artifact.ChannelCount ?? 1,
            CreatedAtUtc = DateTimeOffset.UtcNow,
            Provenance = AppendStretchProvenance(artifact.Provenance, TtsStretchMode.Manual, stretchResult.Engine)
        };
        await mediaAssetRepository.SaveArtifactAsync(updatedArtifact, cancellationToken).ConfigureAwait(false);

        int stretchedDurationSamples = Math.Max(1, (int)Math.Round(analysis.OriginalDurationSeconds * sampleRate));
        TtsTake updatedTake = take.ApplyStretch(
            TtsStretchMode.Manual,
            stretchResult.Engine,
            tempoRatio,
            preStretchDurationSeconds,
            stretchedDurationSamples,
            analysis.OverrunRatio);
        await ttsTakeRepository.SaveAsync(updatedTake, cancellationToken).ConfigureAwait(false);
    }

    public IReadOnlyList<TtsSegmentState> BuildTtsSegmentStates(
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<TranslatedSegment> translatedSegments,
        IReadOnlyList<TtsTake> ttsTakes,
        IReadOnlyList<ProjectArtifact> artifacts,
        IReadOnlyList<StageRunRecord> stageRuns)
    {
        Dictionary<int, TranscriptSegment> sourceSegmentsByIndex = transcriptSegments
            .ToDictionary(segment => segment.SegmentIndex);
        Dictionary<Guid, ProjectArtifact> artifactsById = artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.TtsTake)
            .ToDictionary(artifact => artifact.Id);
        Dictionary<int, TtsTake> latestTakesBySegmentIndex = ttsTakes
            .GroupBy(take => take.SegmentIndex)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(take => take.CreatedAtUtc)
                    .First());
        Dictionary<Guid, StageRunRecord> stageRunsById = stageRuns
            .ToDictionary(static run => run.Id);

        return translatedSegments
            .OrderBy(segment => segment.SegmentIndex)
            .Select(segment =>
            {
                sourceSegmentsByIndex.TryGetValue(segment.SegmentIndex, out TranscriptSegment? sourceSegment);
                double? originalDurationSeconds = sourceSegment is null
                    ? null
                    : sourceSegment.EndSeconds - sourceSegment.StartSeconds;
                if (!latestTakesBySegmentIndex.TryGetValue(segment.SegmentIndex, out TtsTake? take))
                {
                    return new TtsSegmentState(
                        segment.SegmentIndex,
                        TakeId: null,
                        ArtifactRelativePath: null,
                        Status: null,
                        IsStale: false,
                        DurationSeconds: null,
                        DurationOverrunRatio: null,
                        HasDurationWarning: false,
                        WarningMessage: null,
                        OriginalDurationSeconds: originalDurationSeconds);
                }

                bool isStale = IsTakeStale(take, segment);
                ProjectArtifact? artifact = take.ArtifactId is Guid artifactId &&
                                            artifactsById.TryGetValue(artifactId, out ProjectArtifact? storedArtifact)
                    ? storedArtifact
                    : null;
                double? durationSeconds = ComputeTakeDurationSeconds(take, artifact);
                var (severity, hasDurationWarning, hasSpeedLimitWarning, canManualStretch) =
                    ComputeDurationAnalysis(sourceSegment, durationSeconds, isStale, take, artifact);
                StageRunRecord? stageRun = take.StageRunId is Guid stageRunId &&
                                           stageRunsById.TryGetValue(stageRunId, out StageRunRecord? linkedRun)
                    ? linkedRun
                    : null;
                return new TtsSegmentState(
                    segment.SegmentIndex,
                    take.Id,
                    artifact?.RelativePath,
                    take.Status,
                    isStale,
                    durationSeconds,
                    take.DurationOverrunRatio,
                    hasDurationWarning || hasSpeedLimitWarning,
                    BuildTtsWarningMessage(hasDurationWarning, hasSpeedLimitWarning),
                    originalDurationSeconds,
                    take.PreStretchDurationSeconds,
                    take.StretchRatioApplied,
                    take.StretchMode,
                    take.StretchEngine,
                    severity,
                    hasSpeedLimitWarning,
                    canManualStretch,
                    take.Provider,
                    take.ModelId,
                    stageRun?.RuntimeInfo?.ModelAlias,
                    stageRun?.RuntimeInfo?.ModelVariant);
            })
            .ToArray();
    }

    private async Task RunTtsForSpeakerAsync(
        TranscriptProjectState currentState,
        Guid speakerId,
        IReadOnlySet<int>? segmentIndices,
        VoiceAssignment? voiceAssignmentOverride,
        string? preferredModelAlias,
        bool useReferenceClipForVoiceCloning,
        ExecutionProviderKind? preferredExecutionProvider,
        bool requirePreferredExecutionProvider,
        string? preferredModelVariantAlias,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        ProjectSpeaker speaker = currentState.Speakers.FirstOrDefault(speaker => speaker.Id == speakerId)
            ?? throw new InvalidOperationException("The selected speaker was not found.");

        TranslationRevision translationRevision = currentState.CurrentTranslationRevision
            ?? throw new InvalidOperationException("Generate or load a translation before starting TTS.");
        if (currentState.TranslatedSegments.Count == 0)
        {
            throw new InvalidOperationException("The current translation revision has no translated segments.");
        }

        VoiceAssignment? persistedAssignment = voiceAssignmentOverride ??
            currentState.VoiceAssignments.FirstOrDefault(candidate => candidate.SpeakerId == speakerId && !candidate.IsFallback);
        VoiceAssignment assignment = persistedAssignment ??
            (useReferenceClipForVoiceCloning
                ? VoiceAssignment.Create(
                    currentState.ProjectState.Project.Id,
                    speakerId,
                    VoiceCloningDefaults.ChatterboxPrimaryAlias,
                    requiresConsent: true)
                : throw new InvalidOperationException("Assign a Kokoro voicepack to the speaker before starting TTS."));
        IReadOnlyList<ProjectArtifact> projectArtifacts = currentState.ProjectState.Artifacts;
        if (useReferenceClipForVoiceCloning)
        {
            PreparedReferenceClip preparedReferenceClip = await EnsureReferenceClipForVoiceCloningAsync(
                currentState,
                assignment,
                cancellationToken).ConfigureAwait(false);
            assignment = preparedReferenceClip.Assignment;
            projectArtifacts = preparedReferenceClip.ProjectArtifacts;
        }

        PipelineProgressReporter.Phase(
            progress,
            StageNames.Tts,
            "Preparing speaker",
            currentItemLabel: speaker.DisplayName);
        await startTtsStageHandler.HandleAsync(
            new StartTtsStageRequest(
                currentState.ProjectState.Project.Id,
                TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState),
                speakerId,
                translationRevision.TargetLanguage,
                assignment,
                currentState.TranscriptSegments,
                currentState.TranslatedSegments,
                segmentIndices,
                preferredModelAlias,
                projectArtifacts,
                useReferenceClipForVoiceCloning,
                preferredExecutionProvider,
                requirePreferredExecutionProvider,
                preferredModelVariantAlias),
            cancellationToken,
            progress).ConfigureAwait(false);
    }

    private static string BuildSpeakerProgressLabel(ProjectSpeaker speaker, int speakerNumber, int speakerCount) =>
        string.IsNullOrWhiteSpace(speaker.DisplayName)
            ? $"Speaker {speakerNumber} of {speakerCount}"
            : $"Speaker {speakerNumber} of {speakerCount}: {speaker.DisplayName}";

    private async Task<PreparedReferenceClip> EnsureReferenceClipForVoiceCloningAsync(
        TranscriptProjectState currentState,
        VoiceAssignment assignment,
        CancellationToken cancellationToken)
    {
        ProjectArtifact? existingReferenceArtifact = await ResolveAssignedReferenceArtifactAsync(
            currentState,
            assignment.ReferenceClipArtifactId,
            cancellationToken).ConfigureAwait(false);
        if (IsManualReferenceClip(existingReferenceArtifact))
        {
            return new PreparedReferenceClip(
                assignment,
                IncludeProjectArtifact(currentState.ProjectState.Artifacts, existingReferenceArtifact!));
        }

        ProjectArtifact? sourceArtifact = TryResolveReferenceClipSourceAudioArtifact(currentState);
        if (sourceArtifact is null)
        {
            if (existingReferenceArtifact is not null)
            {
                return new PreparedReferenceClip(
                    assignment,
                    IncludeProjectArtifact(currentState.ProjectState.Artifacts, existingReferenceArtifact));
            }

            throw new InvalidOperationException("Normalized audio is required for automatic voice clone reference capture.");
        }

        AutoReferenceClipPlan singlePlan = BuildAutoReferenceClipPlan(
            currentState,
            assignment.SpeakerId,
            sourceArtifact,
            mode: "single");
        AutoReferenceClipPlan packedPlan = BuildAutoReferenceClipPlan(
            currentState,
            assignment.SpeakerId,
            sourceArtifact,
            mode: "packed");
        if (existingReferenceArtifact is not null &&
            (IsCurrentAutoReferenceClip(existingReferenceArtifact, singlePlan.Fingerprint) ||
             IsCurrentAutoReferenceClip(existingReferenceArtifact, packedPlan.Fingerprint)) &&
            File.Exists(artifactStore.GetPath(existingReferenceArtifact.RelativePath)))
        {
            return new PreparedReferenceClip(
                assignment,
                IncludeProjectArtifact(currentState.ProjectState.Artifacts, existingReferenceArtifact));
        }

        if (audioClipExtractor is null)
        {
            throw new InvalidOperationException("Automatic voice clone reference capture is not configured.");
        }

        if (referenceClipAnalyzer is null)
        {
            throw new InvalidOperationException("Reference clip active-speech validation is not configured.");
        }

        ProjectArtifact referenceArtifact = await CaptureAutoReferenceClipAsync(
            currentState,
            assignment.SpeakerId,
            singlePlan,
            cancellationToken).ConfigureAwait(false);
        VoiceAssignment updatedAssignment = await AssignReferenceClipArtifactAsync(
            currentState.ProjectState.Project.Id,
            assignment,
            referenceArtifact.Id,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectArtifact> refreshedArtifacts = await mediaAssetRepository
            .GetArtifactsAsync(currentState.ProjectState.Project.Id, cancellationToken)
            .ConfigureAwait(false);
        if (!refreshedArtifacts.Any(artifact => artifact.Id == referenceArtifact.Id))
        {
            refreshedArtifacts = currentState.ProjectState.Artifacts
                .Where(artifact => artifact.Id != referenceArtifact.Id)
                .Concat([referenceArtifact])
                .ToArray();
        }

        return new PreparedReferenceClip(updatedAssignment, refreshedArtifacts);
    }

    private async Task<ProjectArtifact?> ResolveAssignedReferenceArtifactAsync(
        TranscriptProjectState currentState,
        Guid? referenceClipArtifactId,
        CancellationToken cancellationToken)
    {
        if (referenceClipArtifactId is not Guid artifactId)
        {
            return null;
        }

        ProjectArtifact? stateArtifact = currentState.ProjectState.Artifacts
            .FirstOrDefault(artifact => artifact.Id == artifactId && artifact.Kind == ArtifactKind.ReferenceClip);
        if (stateArtifact is not null)
        {
            return stateArtifact;
        }

        IReadOnlyList<ProjectArtifact> repositoryArtifacts = await mediaAssetRepository
            .GetArtifactsAsync(currentState.ProjectState.Project.Id, cancellationToken)
            .ConfigureAwait(false);
        return repositoryArtifacts
            .FirstOrDefault(artifact => artifact.Id == artifactId && artifact.Kind == ArtifactKind.ReferenceClip);
    }

    private async Task<ProjectArtifact> CaptureAutoReferenceClipAsync(
        TranscriptProjectState currentState,
        Guid speakerId,
        AutoReferenceClipPlan singlePlan,
        CancellationToken cancellationToken)
    {
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        string relativePath = ProjectArtifactPaths.GetReferenceClipRelativePath(speakerId, now);
        await using var tx = new ArtifactWriteTransaction(artifactStore.CreateWriteHandle(relativePath));
        Guid? savedArtifactId = null;

        try
        {
            AutoReferenceClipExtraction extraction = await ExtractAndAnalyzeAutoReferenceAsync(
                singlePlan,
                tx.TemporaryPath,
                cancellationToken).ConfigureAwait(false);
            AutoReferenceClipPlan selectedPlan = singlePlan;
            if (extraction.Analysis.ActiveSpeechSeconds < ReferenceClipPolicy.MinimumActiveSpeechSeconds)
            {
                selectedPlan = BuildAutoReferenceClipPlan(
                    currentState,
                    speakerId,
                    singlePlan.SourceArtifact,
                    mode: "packed");
                extraction = await ExtractAndAnalyzeAutoReferenceAsync(
                    selectedPlan,
                    tx.TemporaryPath,
                    cancellationToken).ConfigureAwait(false);
            }

            if (extraction.Analysis.ActiveSpeechSeconds < ReferenceClipPolicy.MinimumActiveSpeechSeconds)
            {
                throw new InvalidOperationException(
                    $"Automatic voice clone reference capture needs at least {ReferenceClipPolicy.MinimumActiveSpeechSeconds:F1} seconds of active speech for this speaker; detected {extraction.Analysis.ActiveSpeechSeconds:F2} seconds. Upload a reference clip or assign more speech to this speaker.");
            }

            await tx.CommitAsync(artifactStore, cancellationToken).ConfigureAwait(false);
            FileFingerprint fingerprint = await fileFingerprintService
                .ComputeAsync(artifactStore.GetPath(relativePath), cancellationToken)
                .ConfigureAwait(false);
            var artifact = new ProjectArtifact(
                Guid.NewGuid(),
                currentState.ProjectState.Project.Id,
                mediaAsset.Id,
                ArtifactKind.ReferenceClip,
                relativePath,
                fingerprint.Sha256,
                fingerprint.SizeBytes,
                extraction.Analysis.TotalDurationSeconds,
                extraction.Analysis.SampleRate,
                extraction.Analysis.ChannelCount,
                now,
                StageRunId: null,
                Provenance: BuildAutoReferenceProvenance(speakerId, selectedPlan, extraction.Analysis));
            await mediaAssetRepository.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
            savedArtifactId = artifact.Id;
            return artifact;
        }
        catch
        {
            await DeleteSavedReferenceClipArtifactBestEffortAsync(savedArtifactId).ConfigureAwait(false);
            DeleteCommittedReferenceClipFileBestEffort(relativePath);
            throw;
        }
    }

    private async Task<AutoReferenceClipExtraction> ExtractAndAnalyzeAutoReferenceAsync(
        AutoReferenceClipPlan plan,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        AudioClipExtractionResult extractionResult = await audioClipExtractor!.ExtractAsync(
            artifactStore.GetPath(plan.SourceArtifact.RelativePath),
            plan.Ranges,
            destinationPath,
            cancellationToken).ConfigureAwait(false);
        _ = await referenceClipTrimmer.TrimAsync(destinationPath, cancellationToken).ConfigureAwait(false);
        ReferenceClipAnalysis analysis = await referenceClipAnalyzer!
            .AnalyzeAsync(destinationPath, cancellationToken)
            .ConfigureAwait(false);
        return new AutoReferenceClipExtraction(extractionResult, analysis);
    }

    private AutoReferenceClipPlan BuildAutoReferenceClipPlan(
        TranscriptProjectState state,
        Guid speakerId,
        ProjectArtifact sourceArtifact,
        string mode)
    {
        TranscriptSegment[] assignedSegments = state.TranscriptSegments
            .Where(segment => segment.SpeakerId == speakerId)
            .OrderBy(segment => segment.StartSeconds)
            .ToArray();
        if (assignedSegments.Length == 0)
        {
            throw new InvalidOperationException("No transcript segments are assigned to this speaker; assign speech before generating a voice-cloned dub.");
        }

        AudioClipRange[] ranges = string.Equals(mode, "packed", StringComparison.Ordinal)
            ? BuildPackedAutoReferenceRanges(assignedSegments)
            : [BuildSingleAutoReferenceRange(assignedSegments)];
        string fingerprint = BuildAutoReferenceFingerprint(
            state.CurrentTranscriptRevision?.Id,
            sourceArtifact,
            ranges,
            mode);
        return new AutoReferenceClipPlan(sourceArtifact, ranges, mode, fingerprint);
    }

    private static IReadOnlyList<ProjectArtifact> IncludeProjectArtifact(
        IReadOnlyList<ProjectArtifact> artifacts,
        ProjectArtifact artifact) =>
        artifacts.Any(candidate => candidate.Id == artifact.Id)
            ? artifacts
            : artifacts.Concat([artifact]).ToArray();

    private static AudioClipRange BuildSingleAutoReferenceRange(IReadOnlyList<TranscriptSegment> assignedSegments)
    {
        TranscriptSegment segment = assignedSegments
            .OrderByDescending(candidate => candidate.EndSeconds - candidate.StartSeconds)
            .ThenBy(candidate => candidate.StartSeconds)
            .First();
        double endSeconds = Math.Min(
            segment.EndSeconds,
            segment.StartSeconds + ReferenceClipPolicy.RecommendedMaximumActiveSpeechSeconds);
        return new AudioClipRange(segment.StartSeconds, endSeconds);
    }

    private static AudioClipRange[] BuildPackedAutoReferenceRanges(IReadOnlyList<TranscriptSegment> assignedSegments)
    {
        var ranges = new List<AudioClipRange>();
        double totalDurationSeconds = 0d;
        foreach (TranscriptSegment segment in assignedSegments)
        {
            double availableDurationSeconds = segment.EndSeconds - segment.StartSeconds;
            if (availableDurationSeconds <= 0d)
            {
                continue;
            }

            double remainingDurationSeconds = ReferenceClipPolicy.RecommendedMaximumActiveSpeechSeconds - totalDurationSeconds;
            if (remainingDurationSeconds <= 0d)
            {
                break;
            }

            double durationSeconds = Math.Min(availableDurationSeconds, remainingDurationSeconds);
            ranges.Add(new AudioClipRange(segment.StartSeconds, segment.StartSeconds + durationSeconds));
            totalDurationSeconds += durationSeconds;
        }

        if (ranges.Count == 0)
        {
            throw new InvalidOperationException("No usable transcript segment range is available for automatic voice clone reference capture.");
        }

        return ranges.ToArray();
    }

    private static ProjectArtifact? TryResolveReferenceClipSourceAudioArtifact(TranscriptProjectState state)
    {
        ProjectArtifact? acceptedVocalStem = TranscriptWorkflowUtilities.GetLatestAcceptedVocalStem(state.ProjectState.Artifacts);
        ProjectArtifact? routedArtifact = state.ProjectState.Artifacts
            .FirstOrDefault(artifact => string.Equals(
                artifact.RelativePath,
                state.AsrAudioRelativePath,
                StringComparison.OrdinalIgnoreCase));
        if (routedArtifact is { Kind: ArtifactKind.Vocals } &&
            acceptedVocalStem is not null &&
            string.Equals(routedArtifact.RelativePath, acceptedVocalStem.RelativePath, StringComparison.OrdinalIgnoreCase))
        {
            return routedArtifact;
        }

        if (routedArtifact is { Kind: ArtifactKind.SpeechEnhancedAudio } ||
            (routedArtifact is { Kind: ArtifactKind.SpeechProcessedAudio } && acceptedVocalStem is not null))
        {
            return routedArtifact;
        }

        if (acceptedVocalStem is not null)
        {
            return acceptedVocalStem;
        }

        return TranscriptWorkflowUtilities.GetLatestArtifactByKind(state.ProjectState.Artifacts, ArtifactKind.NormalizedAudio);
    }

    private async Task<VoiceAssignment> AssignReferenceClipArtifactAsync(
        Guid projectId,
        VoiceAssignment existing,
        Guid referenceClipArtifactId,
        CancellationToken cancellationToken)
    {
        VoiceAssignment assignment = existing with
        {
            RequiresConsent = true,
            ReferenceClipArtifactId = referenceClipArtifactId
        };

        await voiceAssignmentRepository.SaveAsync(assignment, cancellationToken).ConfigureAwait(false);
        if (existing.ReferenceClipArtifactId != referenceClipArtifactId)
        {
            await ttsTakeRepository
                .MarkByVoiceAssignmentStaleAsync(projectId, assignment.Id, cancellationToken)
                .ConfigureAwait(false);
        }

        return assignment;
    }

    private async Task DeleteSavedReferenceClipArtifactBestEffortAsync(Guid? artifactId)
    {
        if (artifactId is not Guid savedArtifactId)
        {
            return;
        }

        try
        {
            await mediaAssetRepository.DeleteArtifactAsync(savedArtifactId, CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Preserve the original reference-clip failure; cleanup errors should not replace it.
        }
    }

    private void DeleteCommittedReferenceClipFileBestEffort(string relativePath)
    {
        try
        {
            string committedPath = artifactStore.GetPath(relativePath);
            if (File.Exists(committedPath))
            {
                File.Delete(committedPath);
            }
        }
        catch
        {
            // Preserve the original reference-clip failure; cleanup errors should not replace it.
        }
    }

    private static bool IsManualReferenceClip(ProjectArtifact? artifact) =>
        artifact?.Provenance?.StartsWith("manual-speaker-reference:", StringComparison.OrdinalIgnoreCase) == true;

    private static bool IsCurrentAutoReferenceClip(ProjectArtifact artifact, string expectedFingerprint) =>
        artifact.Provenance?.Contains("auto-speaker-reference:v2", StringComparison.OrdinalIgnoreCase) == true &&
        artifact.Provenance.Contains($"fingerprint:{expectedFingerprint}", StringComparison.OrdinalIgnoreCase);

    private static string BuildAutoReferenceProvenance(
        Guid speakerId,
        AutoReferenceClipPlan plan,
        ReferenceClipAnalysis analysis)
    {
        string ranges = string.Join(
            ',',
            plan.Ranges.Select(range =>
                $"{range.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-{range.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)}"));
        return string.Join(
            ';',
            "auto-speaker-reference:v2",
            $"speaker:{speakerId:D}",
            $"source-artifact:{plan.SourceArtifact.Id:D}",
            $"source-sha:{plan.SourceArtifact.Sha256}",
            $"mode:{plan.Mode}",
            $"ranges:{ranges}",
            $"fingerprint:{plan.Fingerprint}",
            $"active-speech:{analysis.ActiveSpeechSeconds.ToString("0.###", CultureInfo.InvariantCulture)}");
    }

    private static string BuildAutoReferenceFingerprint(
        Guid? transcriptRevisionId,
        ProjectArtifact sourceArtifact,
        IReadOnlyList<AudioClipRange> ranges,
        string mode)
    {
        string raw = string.Join(
            '|',
            transcriptRevisionId?.ToString("D") ?? "none",
            sourceArtifact.Id.ToString("D"),
            sourceArtifact.Sha256,
            mode,
            string.Join(
                ',',
                ranges.Select(range =>
                    $"{range.StartSeconds.ToString("0.###", CultureInfo.InvariantCulture)}-{range.EndSeconds.ToString("0.###", CultureInfo.InvariantCulture)}")));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(raw))).ToLowerInvariant();
    }

    private static bool IsTakeStale(TtsTake take, TranslatedSegment segment)
    {
        bool textChanged = !string.IsNullOrWhiteSpace(take.TranslatedTextHash) &&
                           !string.Equals(
                               take.TranslatedTextHash,
                               TtsTextHash.Compute(segment.SegmentIndex, segment.Text),
                               StringComparison.Ordinal);
        return take.IsStale || textChanged;
    }

    private static double? ComputeTakeDurationSeconds(TtsTake take, ProjectArtifact? artifact)
    {
        return take.DurationSamples is int durationSamples && take.SampleRate is int sampleRate && sampleRate > 0
            ? (double)durationSamples / sampleRate
            : artifact?.DurationSeconds;
    }

    private (TtsDurationSeverity Severity, bool HasDurationWarning, bool HasSpeedLimitWarning, bool CanManualStretch) ComputeDurationAnalysis(
        TranscriptSegment? sourceSegment,
        double? durationSeconds,
        bool isStale,
        TtsTake take,
        ProjectArtifact? artifact)
    {
        double? analysisDurationSeconds = take.PreStretchDurationSeconds ?? durationSeconds;
        DurationAnalysisResult? analysis = sourceSegment is null
            ? null
            : durationAnalysisService.Analyze(sourceSegment, analysisDurationSeconds, timingOptions);
        TtsDurationSeverity severity = take.Status is TtsTakeStatus.Completed && !isStale && analysis is not null
            ? analysis.Severity
            : TtsDurationSeverity.None;
        bool hasSpeedLimitWarning = take.Status is TtsTakeStatus.Completed && !isStale && analysis?.HasSpeedLimitWarning == true;
        bool hasDurationWarning = severity is TtsDurationSeverity.Yellow or TtsDurationSeverity.Red;
        bool canManualStretch = take.Status is TtsTakeStatus.Completed &&
                                !isStale &&
                                take.StretchMode is TtsStretchMode.None &&
                                artifact is not null &&
                                analysis?.IsStretchable == true &&
                                analysis.TempoRatio is double tempoRatio &&
                                Math.Abs(tempoRatio - 1d) >= 0.001d;
        return (severity, hasDurationWarning, hasSpeedLimitWarning, canManualStretch);
    }

    private static string? BuildTtsWarningMessage(bool hasDurationWarning, bool hasSpeedLimitWarning)
    {
        if (!hasDurationWarning && !hasSpeedLimitWarning)
        {
            return null;
        }

        string durationWarning = hasDurationWarning
            ? "TTS duration exceeds the source segment by more than 10%."
            : string.Empty;
        string speedWarning = hasSpeedLimitWarning
            ? "Stretch needs more than 1.5x acceleration; voice quality may degrade."
            : string.Empty;
        return string.Join(' ', new[] { durationWarning, speedWarning }.Where(message => message.Length > 0));
    }

    private static string AppendStretchProvenance(
        string? provenance,
        TtsStretchMode mode,
        TtsStretchEngine engine)
    {
        string stretchProvenance = $"stretch:{mode.ToString().ToLowerInvariant()}:{engine.ToString().ToLowerInvariant()}";
        return string.IsNullOrWhiteSpace(provenance)
            ? stretchProvenance
            : $"{provenance};{stretchProvenance}";
    }

    private sealed record PreparedReferenceClip(
        VoiceAssignment Assignment,
        IReadOnlyList<ProjectArtifact> ProjectArtifacts);

    private sealed record AutoReferenceClipPlan(
        ProjectArtifact SourceArtifact,
        IReadOnlyList<AudioClipRange> Ranges,
        string Mode,
        string Fingerprint);

    private sealed record AutoReferenceClipExtraction(
        AudioClipExtractionResult ExtractionResult,
        ReferenceClipAnalysis Analysis);
}
