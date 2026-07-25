using Trackdub.Contracts.Projects;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Application.Transcripts;

public sealed class ProjectWorkflow(
    ProjectMediaIngestService projectMediaIngestService,
    TranscriptProjectStateService stateService,
    TranscriptGenerationService transcriptGenerationService,
    StemSeparationStageHandler? stemSeparationStageHandler = null,
    SpeechAudioPreparationStageHandler? speechAudioPreparationStageHandler = null,
    PipelineDegradationWriter? degradationWriter = null,
    SpeechAudioEnhancementStageHandler? speechAudioEnhancementStageHandler = null,
    OverlapRescueWorkflow? overlapRescueWorkflow = null)
{
    private const string DialogueIsolationUnavailableCode = "DIALOGUE_ISOLATION_UNAVAILABLE";
    private const string DialogueIsolationUnavailableMessage =
        "Dialogue isolation model unavailable; no clean ambiance track was generated.";

    private readonly ProjectMediaIngestService projectMediaIngestService = projectMediaIngestService ?? throw new ArgumentNullException(nameof(projectMediaIngestService));
    private readonly TranscriptProjectStateService stateService = stateService ?? throw new ArgumentNullException(nameof(stateService));
    private readonly TranscriptGenerationService transcriptGenerationService = transcriptGenerationService ?? throw new ArgumentNullException(nameof(transcriptGenerationService));
    private readonly SpeechAudioEnhancementStageHandler? speechAudioEnhancementStageHandler = speechAudioEnhancementStageHandler;
    private readonly OverlapRescueWorkflow? overlapRescueWorkflow = overlapRescueWorkflow;

    public async Task<TranscriptProjectState> CreateAsync(
        CreateTranscriptProjectRequest request,
        CancellationToken cancellationToken,
        IProgress<StemSeparationProgress>? stemSeparationProgress = null)
    {
        string? sourceLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(request.SourceLanguage);
        CreateProjectFromMediaResult createResult = await projectMediaIngestService.CreateAsync(
            new CreateProjectFromMediaRequest(request.ProjectName, request.SourceMediaPath),
            cancellationToken).ConfigureAwait(false);

        ProjectArtifact? vocalStemArtifact = null;
        var currentArtifacts = new List<ProjectArtifact> { createResult.AudioArtifact };
        if (request.EnableStemSeparation)
        {
            StemSeparationStageResult? stemResult = await TryRunStemSeparationForImportAsync(
                createResult.Project.Id,
                createResult.MediaAsset,
                createResult.StemSeparationSourceAudioArtifact,
                request.ModelPreferences?.SeparationModelAlias,
                request.ModelPreferences?.GetPreferredExecutionProvider(RuntimeStage.Separation),
                request.ModelPreferences?.RequiresPreferredExecutionProvider(RuntimeStage.Separation) == true,
                request.ModelPreferences?.GetPreferredModelVariantAlias(RuntimeStage.Separation),
                stemSeparationProgress,
                cancellationToken).ConfigureAwait(false);
            if (stemResult is not null)
            {
                vocalStemArtifact = stemResult.VocalsArtifact;
                currentArtifacts.AddRange(stemResult.Artifacts);
            }
        }

        TranscriptAudioRoutingPlan audioRoutingPlan = await TryPrepareSpeechAudioAsync(
            createResult.Project.Id,
            createResult.MediaAsset,
            createResult.AudioArtifact,
            vocalStemArtifact,
            currentArtifacts,
            cancellationToken).ConfigureAwait(false);

        await transcriptGenerationService.GenerateTranscriptAsync(
            createResult.Project,
            createResult.MediaAsset,
            createResult.AudioArtifact,
            audioRoutingPlan,
            request.EnableSpeakerDiarization,
            request.ModelPreferences,
            cancellationToken,
            sourceLanguage).ConfigureAwait(false);

        return await ReloadAsync(requestedTranslationTargetLanguage: null, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> CreateMediaSpineAsync(
        CreateTranscriptProjectRequest request,
        CancellationToken cancellationToken)
    {
        await projectMediaIngestService.CreateMediaSpineAsync(
            new CreateProjectFromMediaRequest(request.ProjectName, request.SourceMediaPath),
            cancellationToken).ConfigureAwait(false);

        return await ReloadAsync(requestedTranslationTargetLanguage: null, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts normalized audio and waveform after a lightweight media-spine import.
    /// </summary>
    public async Task<TranscriptProjectState> EnsureNormalizedProjectAudioAsync(
        CancellationToken cancellationToken,
        int? ffmpegThreadBudget = null)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        await projectMediaIngestService.EnsureNormalizedAudioAsync(
            mediaAsset,
            currentState.ProjectState.Artifacts,
            cancellationToken,
            ffmpegThreadBudget).ConfigureAwait(false);

        return await stateService.RefreshArtifactsAndStageRunsAsync(currentState, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RunInitialTranscriptionAsync(
        bool enableSpeakerDiarization,
        InferenceModelPreferences? modelPreferences,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null,
        string? sourceLanguage = null,
        bool enableStemSeparation = false)
    {
        string? normalizedSourceLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(sourceLanguage);
        InferenceModelPreferences preferences = modelPreferences ?? InferenceModelPreferences.Empty;
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        IReadOnlyList<ProjectArtifact> artifacts = currentState.ProjectState.Artifacts;
        PipelineProgressReporter.Phase(progress, StageNames.Asr, "Preparing audio", "Ensuring normalized audio for transcription.");
        ProjectArtifact normalizedAudio = await EnsureNormalizedAudioForPipelineAsync(
            mediaAsset,
            artifacts,
            cancellationToken).ConfigureAwait(false);

        ProjectArtifact? vocalStem = TranscriptWorkflowUtilities.GetLatestAcceptedVocalStem(artifacts);
        if (enableStemSeparation && vocalStem is null)
        {
            PipelineProgressReporter.Phase(progress, StageNames.Separation, "Splitting audio", "Separating dialogue before cleanup.");
            ProjectArtifact stemSourceAudio = await projectMediaIngestService.EnsureStemSeparationAudioAsync(
                mediaAsset,
                artifacts,
                cancellationToken).ConfigureAwait(false);
            if (!artifacts.Any(artifact => artifact.Id == stemSourceAudio.Id))
            {
                artifacts = artifacts.Concat([stemSourceAudio]).ToArray();
            }

            StemSeparationStageResult? stemResult = await TryRunStemSeparationForImportAsync(
                currentState.ProjectState.Project.Id,
                mediaAsset,
                stemSourceAudio,
                preferences.SeparationModelAlias,
                preferences.GetPreferredExecutionProvider(RuntimeStage.Separation),
                preferences.RequiresPreferredExecutionProvider(RuntimeStage.Separation),
                preferences.GetPreferredModelVariantAlias(RuntimeStage.Separation),
                null,
                cancellationToken).ConfigureAwait(false);
            if (stemResult is not null)
            {
                vocalStem = stemResult.VocalsArtifact;
                artifacts = artifacts.Concat(stemResult.Artifacts).ToArray();
            }
        }

        PipelineProgressReporter.Phase(progress, StageNames.Asr, "Routing speech audio", "Preparing the best speech audio for transcription.");
        TranscriptAudioRoutingPlan audioRoutingPlan = await TryPrepareSpeechAudioAsync(
            currentState.ProjectState.Project.Id,
            mediaAsset,
            normalizedAudio,
            vocalStem,
            artifacts,
            cancellationToken).ConfigureAwait(false);

        await transcriptGenerationService.GenerateTranscriptAsync(
            currentState.ProjectState.Project,
            mediaAsset,
            normalizedAudio,
            audioRoutingPlan,
            enableSpeakerDiarization,
            preferences,
            cancellationToken,
            normalizedSourceLanguage,
            progress).ConfigureAwait(false);

        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RunTranscriptStageAsync(
        string stageName,
        bool enableSpeakerDiarization,
        InferenceModelPreferences? modelPreferences,
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null,
        string? sourceLanguage = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageName);
        string? normalizedSourceLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(sourceLanguage);
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        IReadOnlyList<ProjectArtifact> artifacts = currentState.ProjectState.Artifacts;
        ProjectArtifact normalizedAudio = await EnsureNormalizedAudioForPipelineAsync(
            mediaAsset,
            artifacts,
            cancellationToken).ConfigureAwait(false);

        ProjectArtifact? vocalStem = TranscriptWorkflowUtilities.GetLatestAcceptedVocalStem(artifacts);
        PipelineProgressReporter.Phase(progress, stageName, "Routing speech audio", "Preparing the best speech audio for this stage.");
        TranscriptAudioRoutingPlan audioRoutingPlan = await TryPrepareSpeechAudioAsync(
            currentState.ProjectState.Project.Id,
            mediaAsset,
            normalizedAudio,
            vocalStem,
            artifacts,
            cancellationToken).ConfigureAwait(false);

        await transcriptGenerationService.GenerateTranscriptStageAsync(
            currentState.ProjectState.Project,
            mediaAsset,
            normalizedAudio,
            audioRoutingPlan,
            stageName,
            enableSpeakerDiarization,
            modelPreferences ?? InferenceModelPreferences.Empty,
            cancellationToken,
            normalizedSourceLanguage,
            progress).ConfigureAwait(false);

        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public Task<TranscriptProjectState> OpenAsync(CancellationToken cancellationToken) =>
        ReloadAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    public Task<TranscriptProjectState> OpenProjectShellAsync(CancellationToken cancellationToken) =>
        stateService.OpenProjectShellAsync(requestedTranslationTargetLanguage: null, cancellationToken);

    public Task<TranscriptProjectState> ReloadAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        stateService.OpenAsync(requestedTranslationTargetLanguage, cancellationToken);

    public async Task<TranscriptProjectState> SaveUiSettingsAsync(
        ProjectUiSettings settings,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);

        await projectMediaIngestService.SaveUiSettingsAsync(settings, cancellationToken).ConfigureAwait(false);

        return await ReloadAsync(settings.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RelocateSourceAsync(
        RelocateTranscriptSourceRequest request,
        CancellationToken cancellationToken)
    {
        await projectMediaIngestService.RelocateSourceAsync(
            new RelocateSourceMediaRequest(request.NewSourceMediaPath),
            cancellationToken).ConfigureAwait(false);

        return await ReloadAsync(request.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RenameProjectAsync(
        RenameProjectRequest request,
        CancellationToken cancellationToken)
    {
        await projectMediaIngestService.RenameProjectAsync(request, cancellationToken).ConfigureAwait(false);

        return await ReloadAsync(request.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RunStemSeparationAsync(
        CancellationToken cancellationToken,
        IProgress<StemSeparationProgress>? progress = null,
        string? preferredModelAlias = null,
        InferenceModelPreferences? modelPreferences = null,
        bool regenerateTranscript = true)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        ProjectArtifact sourceAudioArtifact = await projectMediaIngestService
            .EnsureStemSeparationAudioAsync(mediaAsset, currentState.ProjectState.Artifacts, cancellationToken)
            .ConfigureAwait(false);

        if (stemSeparationStageHandler is null)
        {
            throw new InvalidOperationException("Stem separation is not configured.");
        }

        StemSeparationStageResult stemResult;
        try
        {
            stemResult = await stemSeparationStageHandler.HandleAsync(
                new StemSeparationStageRequest(
                    currentState.ProjectState.Project.Id,
                    mediaAsset,
                    sourceAudioArtifact,
                    currentState.ProjectState.Artifacts,
                    preferredModelAlias,
                    modelPreferences?.GetPreferredExecutionProvider(RuntimeStage.Separation),
                    modelPreferences?.RequiresPreferredExecutionProvider(RuntimeStage.Separation) == true,
                    modelPreferences?.GetPreferredModelVariantAlias(RuntimeStage.Separation)),
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            await WriteDialogueIsolationUnavailableAsync(
                currentState.ProjectState.Project.Id,
                mediaAsset.Id,
                ex.Message,
                CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (regenerateTranscript)
        {
            TranscriptAudioRoutingPlan audioRoutingPlan = await TryPrepareSpeechAudioAsync(
                currentState.ProjectState.Project.Id,
                mediaAsset,
                sourceAudioArtifact,
                stemResult.VocalsArtifact,
                currentState.ProjectState.Artifacts
                    .Concat(stemResult.Artifacts)
                    .ToArray(),
                cancellationToken).ConfigureAwait(false);

            if (ShouldRegenerateTranscriptAfterStemRerun(currentState))
            {
                await transcriptGenerationService.GenerateTranscriptAsync(
                    currentState.ProjectState.Project,
                    mediaAsset,
                    sourceAudioArtifact,
                    audioRoutingPlan,
                    ShouldRegenerateWithDiarization(currentState),
                    modelPreferences ?? InferenceModelPreferences.Empty,
                    cancellationToken,
                    sourceLanguage: null,
                    forceRerun: true).ConfigureAwait(false);
            }
        }

        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RunSpeechAudioPreparationAsync(
        CancellationToken cancellationToken,
        IProgress<PipelineProgressEvent>? progress = null)
    {
        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        MediaAsset mediaAsset = TranscriptWorkflowUtilities.GetRequiredMediaAsset(currentState);
        IReadOnlyList<ProjectArtifact> artifacts = currentState.ProjectState.Artifacts;
        PipelineProgressReporter.Started(progress, StageNames.SpeechEnhancement, "Starting speech audio cleanup.");
        PipelineProgressReporter.Phase(progress, StageNames.SpeechEnhancement, "Preparing audio", "Ensuring normalized audio for cleanup.");
        ProjectArtifact normalizedAudio = await EnsureNormalizedAudioForPipelineAsync(
            mediaAsset,
            artifacts,
            cancellationToken).ConfigureAwait(false);

        ProjectArtifact? vocalStem = TranscriptWorkflowUtilities.GetLatestAcceptedVocalStem(artifacts);
        PipelineProgressReporter.Phase(progress, StageNames.SpeechEnhancement, "Processing speech audio", "Running speech audio preparation.");
        await TryPrepareSpeechAudioAsync(
            currentState.ProjectState.Project.Id,
            mediaAsset,
            normalizedAudio,
            vocalStem,
            artifacts,
            cancellationToken).ConfigureAwait(false);

        PipelineProgressReporter.Completed(progress, StageNames.SpeechEnhancement, TimeSpan.Zero, "Speech audio cleanup finished.");
        return await stateService.RefreshArtifactsAndStageRunsAsync(currentState, cancellationToken).ConfigureAwait(false);
    }

    public async Task<TranscriptProjectState> RunOverlapRescueAsync(
        CancellationToken cancellationToken,
        IProgress<OverlapRescueProgress>? progress = null,
        string? preferredModelAlias = null,
        InferenceModelPreferences? modelPreferences = null,
        bool retranscribeCandidates = false)
    {
        if (overlapRescueWorkflow is null)
        {
            throw new InvalidOperationException("Overlap speech rescue is not configured.");
        }

        TranscriptProjectState currentState = await OpenAsync(cancellationToken).ConfigureAwait(false);
        await overlapRescueWorkflow
            .RunAsync(
                currentState,
                cancellationToken,
                progress,
                preferredModelAlias,
                modelPreferences,
                retranscribeCandidates)
            .ConfigureAwait(false);

        return await ReloadAsync(currentState.SelectedTranslationTargetLanguage, cancellationToken).ConfigureAwait(false);
    }

    private async Task<StemSeparationStageResult?> TryRunStemSeparationForImportAsync(
        Guid projectId,
        MediaAsset mediaAsset,
        ProjectArtifact sourceAudioArtifact,
        string? preferredModelAlias,
        ExecutionProviderKind? preferredExecutionProvider,
        bool requirePreferredExecutionProvider,
        string? preferredModelVariantAlias,
        IProgress<StemSeparationProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (stemSeparationStageHandler is null)
        {
            return null;
        }

        try
        {
            return await stemSeparationStageHandler.HandleAsync(
                new StemSeparationStageRequest(
                    projectId,
                    mediaAsset,
                    sourceAudioArtifact,
                    [sourceAudioArtifact],
                    preferredModelAlias,
                    preferredExecutionProvider,
                    requirePreferredExecutionProvider,
                    preferredModelVariantAlias),
                progress,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            if (requirePreferredExecutionProvider)
            {
                throw;
            }

            await WriteDialogueIsolationUnavailableAsync(
                projectId,
                mediaAsset.Id,
                ex.Message,
                cancellationToken: CancellationToken.None).ConfigureAwait(false);

            return null;
        }
    }

    private async Task WriteDialogueIsolationUnavailableAsync(
        Guid projectId,
        Guid mediaAssetId,
        string? detail,
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
                    StageNames.Separation,
                    DialogueIsolationUnavailableCode,
                    DialogueIsolationUnavailableMessage,
                    Detail: detail,
                    SelectedFallback: "raw-audio",
                    RecommendedAction: "Set up a separation model and regenerate stems.",
                    DateTimeOffset.UtcNow,
                    StageRunId: null),
                projectId,
                mediaAssetId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Degradation write is best-effort; failure must not abort the raw-audio fallback.
        }
    }

    private async Task WriteEnhancementFallbackAsync(
        Guid projectId,
        Guid mediaAssetId,
        string? detail,
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
                    StageNames.SpeechEnhancement,
                    "SPEECH_ENHANCEMENT_FAILED",
                    "Speech enhancement failed; the unenhanced audio will be used for transcription.",
                    Detail: detail,
                    SelectedFallback: "unenhanced-audio",
                    RecommendedAction: null,
                    DateTimeOffset.UtcNow,
                    StageRunId: null),
                projectId,
                mediaAssetId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            // Degradation write is best-effort.
        }
    }

    private Task<ProjectArtifact> EnsureNormalizedAudioForPipelineAsync(
        MediaAsset mediaAsset,
        IReadOnlyList<ProjectArtifact> artifacts,
        CancellationToken cancellationToken) =>
        projectMediaIngestService.EnsureNormalizedAudioAsync(mediaAsset, artifacts, cancellationToken);

    private async Task<TranscriptAudioRoutingPlan> TryPrepareSpeechAudioAsync(
        Guid projectId,
        MediaAsset mediaAsset,
        ProjectArtifact normalizedAudioArtifact,
        ProjectArtifact? vocalStemArtifact,
        IReadOnlyList<ProjectArtifact> existingArtifacts,
        CancellationToken cancellationToken)
    {
        ProjectArtifact selectedSource = vocalStemArtifact ?? normalizedAudioArtifact;
        SpeechAudioSourceKind sourceKind = vocalStemArtifact is null
            ? SpeechAudioSourceKind.FullMix
            : SpeechAudioSourceKind.VocalStem;

        // If speech processed audio already exists, skip both enhancement and preparation.
        ProjectArtifact? existingProcessed = existingArtifacts
            .Where(a => a.Kind == ArtifactKind.SpeechProcessedAudio)
            .OrderByDescending(a => a.CreatedAtUtc)
            .FirstOrDefault();
        if (existingProcessed is not null)
        {
            return TranscriptAudioRoutingPlan.Raw(existingProcessed, sourceKind);
        }

        SpeechAudioEnhancementStageResult? enhancementResult = null;

        // Skip enhancement if a SpeechEnhancedAudio artifact already exists for this project.
        ProjectArtifact? existingEnhanced = existingArtifacts
            .Where(a => a.Kind == ArtifactKind.SpeechEnhancedAudio)
            .OrderByDescending(a => a.CreatedAtUtc)
            .FirstOrDefault();
        if (speechAudioEnhancementStageHandler is not null && existingEnhanced is null)
        {
            try
            {
                enhancementResult = await speechAudioEnhancementStageHandler
                    .HandleAsync(
                        new SpeechAudioEnhancementStageRequest(
                            projectId,
                            mediaAsset,
                            selectedSource,
                            existingArtifacts),
                        cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
            {
                await WriteEnhancementFallbackAsync(projectId, mediaAsset.Id, ex.Message, cancellationToken)
                    .ConfigureAwait(false);
            }
        }

        if (speechAudioPreparationStageHandler is null)
        {
            ProjectArtifact fallbackSource = enhancementResult?.EnhancedAudioArtifact
                ?? existingEnhanced
                ?? selectedSource;
            return TranscriptAudioRoutingPlan.Raw(fallbackSource, sourceKind);
        }

        ProjectArtifact prepNormalizedAudio = normalizedAudioArtifact;
        ProjectArtifact? prepVocalStem = vocalStemArtifact;
        if (enhancementResult?.EnhancedAudioArtifact is ProjectArtifact enhancedAudio)
        {
            if (vocalStemArtifact is not null)
            {
                prepVocalStem = enhancedAudio;
            }
            else
            {
                prepNormalizedAudio = enhancedAudio;
            }
        }
        else if (existingEnhanced is not null)
        {
            if (vocalStemArtifact is not null)
            {
                prepVocalStem = existingEnhanced;
            }
            else
            {
                prepNormalizedAudio = existingEnhanced;
            }
        }

        try
        {
            return await speechAudioPreparationStageHandler
                .HandleAsync(
                    new SpeechAudioPreparationStageRequest(
                        projectId,
                        mediaAsset,
                        prepNormalizedAudio,
                        prepVocalStem,
                        existingArtifacts),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not TaskCanceledException)
        {
            ProjectArtifact fallbackSource = enhancementResult?.EnhancedAudioArtifact
                ?? existingEnhanced
                ?? selectedSource;
            return TranscriptAudioRoutingPlan.Raw(fallbackSource, sourceKind);
        }
    }

    private static bool ShouldRegenerateWithDiarization(TranscriptProjectState currentState) =>
        currentState.StageRuns.Any(static stageRun =>
            string.Equals(stageRun.StageName, StageNames.Diarization, StringComparison.OrdinalIgnoreCase));

    internal static bool ShouldRegenerateTranscriptAfterStemRerun(TranscriptProjectState currentState)
    {
        // Generated transcript revisions carry an ASR stage run id; editor-created
        // revisions do not. Preserve user edits without treating generated reruns
        // as manual changes.
        if (currentState.CurrentTranscriptRevision is { StageRunId: null })
        {
            return false;
        }

        // Re-running diarization over an already diarized, assigned revision can
        // invalidate speaker assignments. Keep that transcript intact while still
        // allowing no-turn diarization retries and generated single-speaker reruns.
        return currentState.SpeakerTurns.Count == 0 ||
               !currentState.TranscriptSegments.Any(static segment => segment.SpeakerId is not null);
    }
}
