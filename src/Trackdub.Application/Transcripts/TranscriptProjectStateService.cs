using Trackdub.Contracts;
using Trackdub.Application.LipSynthesis;
using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Contracts.Projects;
using Trackdub.Application.Runtime;
using Trackdub.Contracts.LipSync;
using Trackdub.Contracts.LipSynthesis;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.LipSync;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;

namespace Trackdub.Application.Transcripts;

public sealed class TranscriptProjectStateService(
    ProjectMediaIngestService projectMediaIngestService,
    ITranscriptRepository transcriptRepository,
    ITranslationRepository translationRepository,
    IProjectStageRunStore stageRunStore,
    ISpeakerRepository speakerRepository,
    IVoiceAssignmentRepository voiceAssignmentRepository,
    ITtsTakeRepository ttsTakeRepository,
    ITranslationLanguageRouter translationLanguageRouter,
    IVoiceCatalog voiceCatalog,
    IArtifactStore artifactStore,
    TtsOrchestrationService ttsOrchestrationService,
    VoiceAssignmentService voiceAssignmentService,
    MixPlanStore? mixPlanStore = null,
    IRuntimeSelectionService? selectionService = null,
    IExportToolAvailabilityService? exportToolAvailabilityService = null,
    ITtsCandidateGroupRepository? candidateGroupRepository = null,
    ILipSyncSegmentRepository? lipSyncSegmentRepository = null,
    ILipSynthesisSegmentRepository? lipSynthesisSegmentRepository = null,
    IApplicationLogger? logger = null)
{
    private readonly ProjectMediaIngestService projectMediaIngestService = projectMediaIngestService ?? throw new ArgumentNullException(nameof(projectMediaIngestService));
    private readonly ITranscriptRepository transcriptRepository = transcriptRepository ?? throw new ArgumentNullException(nameof(transcriptRepository));
    private readonly ITranslationRepository translationRepository = translationRepository ?? throw new ArgumentNullException(nameof(translationRepository));
    private readonly IProjectStageRunStore stageRunStore = stageRunStore ?? throw new ArgumentNullException(nameof(stageRunStore));
    private readonly ISpeakerRepository speakerRepository = speakerRepository ?? throw new ArgumentNullException(nameof(speakerRepository));
    private readonly IVoiceAssignmentRepository voiceAssignmentRepository = voiceAssignmentRepository ?? throw new ArgumentNullException(nameof(voiceAssignmentRepository));
    private readonly ITtsTakeRepository ttsTakeRepository = ttsTakeRepository ?? throw new ArgumentNullException(nameof(ttsTakeRepository));
    private readonly ITranslationLanguageRouter translationLanguageRouter = translationLanguageRouter ?? throw new ArgumentNullException(nameof(translationLanguageRouter));
    private readonly IVoiceCatalog voiceCatalog = voiceCatalog ?? throw new ArgumentNullException(nameof(voiceCatalog));
    private readonly IArtifactStore artifactStore = artifactStore ?? throw new ArgumentNullException(nameof(artifactStore));
    private readonly ITtsCandidateGroupRepository? candidateGroupRepository = candidateGroupRepository;
    private readonly ILipSyncSegmentRepository? lipSyncSegmentRepository = lipSyncSegmentRepository;
    private readonly ILipSynthesisSegmentRepository? lipSynthesisSegmentRepository = lipSynthesisSegmentRepository;
    private readonly MixPlanStore mixPlanStore = mixPlanStore ?? new MixPlanStore(artifactStore);
    private readonly TtsOrchestrationService ttsOrchestrationService = ttsOrchestrationService ?? throw new ArgumentNullException(nameof(ttsOrchestrationService));
    private readonly VoiceAssignmentService voiceAssignmentService = voiceAssignmentService ?? throw new ArgumentNullException(nameof(voiceAssignmentService));
    private readonly IApplicationLogger? logger = logger;

    public async Task<TranscriptProjectState> OpenAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        await OpenInternalAsync(
            requestedTranslationTargetLanguage,
            ProjectOpenReadProfile.Full,
            cancellationToken).ConfigureAwait(false);

    public Task<TranscriptProjectState> OpenProjectShellAsync(
        string? requestedTranslationTargetLanguage,
        CancellationToken cancellationToken) =>
        OpenInternalAsync(
            requestedTranslationTargetLanguage,
            ProjectOpenReadProfile.Shell,
            cancellationToken);

    private async Task<TranscriptProjectState> OpenInternalAsync(
        string? requestedTranslationTargetLanguage,
        ProjectOpenReadProfile profile,
        CancellationToken cancellationToken)
    {
        OpenProjectResult openResult = await projectMediaIngestService.OpenAsync(cancellationToken).ConfigureAwait(false);
        TranscriptRevision? currentRevision = await transcriptRepository.GetCurrentRevisionAsync(
            openResult.Project.Id,
            cancellationToken).ConfigureAwait(false);
        IReadOnlyList<ProjectSpeaker> speakers = await LoadOrCreateSpeakersAsync(
            openResult.Project.Id,
            currentRevision,
            cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TranscriptSegment> storedSegments = currentRevision is null
            ? []
            : await transcriptRepository.GetSegmentsAsync(currentRevision.Id, cancellationToken).ConfigureAwait(false);
        IReadOnlyList<TranscriptSegment> segments = TranscriptWorkflowUtilities.ApplySingleSpeakerDefaultAssignments(storedSegments, speakers);
        IReadOnlyList<SpeakerTurn> speakerTurns = await speakerRepository.ListTurnsAsync(
            openResult.Project.Id,
            cancellationToken).ConfigureAwait(false);

        string? normalizedTranscriptLanguage = TranscriptWorkflowUtilities.NormalizeTranscriptLanguageCode(openResult.TranscriptLanguage);
        IReadOnlyList<TranslationTargetLanguageOption> supportedTargetLanguages = normalizedTranscriptLanguage is null
            ? []
            : await translationLanguageRouter.GetSupportedTargetLanguagesAsync(
                normalizedTranscriptLanguage,
                cancellationToken).ConfigureAwait(false);
        string? persistedTranslationTargetLanguage =
            TranscriptWorkflowUtilities.NormalizeTranslationTargetLanguageCodeOrNull(
                openResult.UiSettings?.SelectedTranslationTargetLanguage);
        string? selectedTranslationTargetLanguage = TranscriptWorkflowUtilities.ResolveSelectedTranslationTargetLanguage(
            normalizedTranscriptLanguage,
            requestedTranslationTargetLanguage ?? persistedTranslationTargetLanguage,
            supportedTargetLanguages);

        TranslationRevision? currentTranslationRevision = selectedTranslationTargetLanguage is null
            ? null
            : await translationRepository.GetCurrentRevisionAsync(
                openResult.Project.Id,
                selectedTranslationTargetLanguage,
                cancellationToken).ConfigureAwait(false);

        IReadOnlyList<TranslatedSegment> translatedSegments = currentTranslationRevision is null
            ? []
            : await translationRepository.GetSegmentsAsync(currentTranslationRevision.Id, cancellationToken).ConfigureAwait(false);

        IReadOnlyList<StageRunRecord> rawStageRuns = await stageRunStore.ListByProjectAsync(
            openResult.Project.Id,
            cancellationToken).ConfigureAwait(false);

        rawStageRuns = await StageRunHygiene.ReconcileStaleRunningAsync(
            stageRunStore,
            rawStageRuns,
            logger,
            cancellationToken).ConfigureAwait(false);

        List<StageRunRecord> stageRuns = new();
        if (selectionService != null)
        {
            var capabilities = await selectionService.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            var supportedProviders = capabilities.Where(c => c.ProviderLoadable).Select(c => c.Provider.ToString()).ToHashSet();

            foreach (var run in rawStageRuns)
            {
                if (run.RuntimeInfo != null && !supportedProviders.Contains(run.RuntimeInfo.SelectedProvider))
                {
                    // Logical downgrade for UI display - do not persist, just inform the user in this session
                    string nextBestProvider = supportedProviders.Contains(ExecutionProviderKind.DirectMl.ToString())
                        ? ExecutionProviderKind.DirectMl.ToString()
                        : ExecutionProviderKind.Cpu.ToString();
                    var downgradedInfo = run.RuntimeInfo with
                    {
                        SelectedProvider = nextBestProvider,
                        FallbackReason = $"Hardware changed: {run.RuntimeInfo.SelectedProvider} is no longer available on this machine. Results are preserved but future runs will use {nextBestProvider}."
                    };
                    stageRuns.Add(run with { RuntimeInfo = downgradedInfo });
                }
                else
                {
                    stageRuns.Add(run);
                }
            }
        }
        else
        {
            stageRuns.AddRange(rawStageRuns);
        }

        IReadOnlySet<int> staleTranslatedSegmentIndices = TranscriptWorkflowUtilities.BuildStaleTranslatedSegmentIndices(
            currentRevision,
            segments,
            currentTranslationRevision,
            translatedSegments);
        bool isTranslationStale = staleTranslatedSegmentIndices.Count > 0;
        IReadOnlyList<VoiceAssignment> voiceAssignments = await voiceAssignmentRepository
            .GetAllAsync(openResult.Project.Id, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<VoiceCatalogEntry> availableVoices = voiceCatalog.GetVoices();
        IReadOnlyList<TtsTake> ttsTakes = await ttsTakeRepository
            .GetByProjectAsync(openResult.Project.Id, cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<TtsCandidateGroup> candidateGroups = profile.IncludeTtsCandidates && candidateGroupRepository is not null
            ? await candidateGroupRepository.GetByProjectAsync(openResult.Project.Id, cancellationToken)
                .ConfigureAwait(false)
            : [];
        IReadOnlyList<TtsSegmentState> ttsSegmentStates = ttsOrchestrationService.BuildTtsSegmentStates(
            segments,
            translatedSegments,
            ttsTakes,
            openResult.Artifacts,
            stageRuns);
        IReadOnlyList<VoiceAssignmentWarning> voiceAssignmentWarnings = voiceAssignmentService.BuildWarnings(
            voiceAssignments,
            availableVoices,
            selectedTranslationTargetLanguage);
        StemAudioRoute stemAudioRoute = TranscriptWorkflowUtilities.BuildStemAudioRoute(openResult.Artifacts, stageRuns);
        IReadOnlyList<LipSyncSegmentState>? lipSyncSegmentStates = profile.IncludeLipSyncStates
            ? await BuildLipSyncSegmentStatesAsync(
                openResult.Project.Id,
                translatedSegments,
                cancellationToken).ConfigureAwait(false)
            : null;
        IReadOnlyList<LipSynthesisSegmentUiState>? lipSynthesisSegmentStates = profile.IncludeLipSynthesisStates
            ? await BuildLipSynthesisSegmentUiStatesAsync(
                openResult.Project.Id,
                segments,
                speakerTurns,
                cancellationToken).ConfigureAwait(false)
            : null;

        return new TranscriptProjectState(
            openResult,
            currentRevision,
            segments,
            speakers,
            speakerTurns,
            currentTranslationRevision,
            translatedSegments,
            isTranslationStale,
            openResult.TranscriptLanguage,
            stageRuns,
            supportedTargetLanguages,
            selectedTranslationTargetLanguage,
            staleTranslatedSegmentIndices,
            profile.IncludeWaveformSummary
                ? await ReadWaveformSummaryAsync(cancellationToken).ConfigureAwait(false)
                : null,
            availableVoices,
            voiceAssignments,
            ttsTakes,
            ttsSegmentStates,
            voiceAssignmentWarnings,
            stemAudioRoute.AsrAudioRelativePath,
            stemAudioRoute.MixSourceAudioRelativePath,
            stemAudioRoute.WarningMessage,
            profile.IncludeMixPlan
                ? await mixPlanStore.LoadAsync(cancellationToken).ConfigureAwait(false)
                : null,
            GetExportToolAvailability(),
            openResult.UiSettings,
            candidateGroups,
            lipSyncSegmentStates,
            lipSynthesisSegmentStates);
    }

    /// <summary>
    /// Refreshes only the artifact, stage-run, and waveform fields of an existing state snapshot.
    /// Use for pipeline methods that write artifacts and stage runs but leave all other state unchanged.
    /// Costs ~3-4 queries instead of the 15+ issued by <see cref="OpenAsync"/>.
    /// </summary>
    public async Task<TranscriptProjectState> RefreshArtifactsAndStageRunsAsync(
        TranscriptProjectState existingState,
        CancellationToken cancellationToken)
    {
        Guid projectId = existingState.ProjectState.Project.Id;

        IReadOnlyList<ProjectArtifact> artifacts = await projectMediaIngestService
            .GetProjectArtifactsAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        IReadOnlyList<StageRunRecord> rawStageRuns = await stageRunStore
            .ListByProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);
        rawStageRuns = await StageRunHygiene.ReconcileStaleRunningAsync(
            stageRunStore, rawStageRuns, logger, cancellationToken)
            .ConfigureAwait(false);

        List<StageRunRecord> stageRuns = new();
        if (selectionService != null)
        {
            var capabilities = await selectionService.GetCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
            var supportedProviders = capabilities.Where(c => c.ProviderLoadable).Select(c => c.Provider.ToString()).ToHashSet();
            foreach (var run in rawStageRuns)
            {
                if (run.RuntimeInfo != null && !supportedProviders.Contains(run.RuntimeInfo.SelectedProvider))
                {
                    string nextBestProvider = supportedProviders.Contains(ExecutionProviderKind.DirectMl.ToString())
                        ? ExecutionProviderKind.DirectMl.ToString()
                        : ExecutionProviderKind.Cpu.ToString();
                    stageRuns.Add(run with
                    {
                        RuntimeInfo = run.RuntimeInfo with
                        {
                            SelectedProvider = nextBestProvider,
                            FallbackReason = $"Hardware changed: {run.RuntimeInfo.SelectedProvider} is no longer available on this machine. Results are preserved but future runs will use {nextBestProvider}."
                        }
                    });
                }
                else
                {
                    stageRuns.Add(run);
                }
            }
        }
        else
        {
            stageRuns.AddRange(rawStageRuns);
        }

        StemAudioRoute stemAudioRoute = TranscriptWorkflowUtilities.BuildStemAudioRoute(artifacts, stageRuns);

        return existingState with
        {
            ProjectState = existingState.ProjectState with { Artifacts = artifacts },
            StageRuns = stageRuns,
            WaveformSummary = await ReadWaveformSummaryAsync(cancellationToken).ConfigureAwait(false),
            AsrAudioRelativePath = stemAudioRoute.AsrAudioRelativePath,
            MixSourceAudioRelativePath = stemAudioRoute.MixSourceAudioRelativePath,
            StemSeparationWarning = stemAudioRoute.WarningMessage,
        };
    }

    private ExportToolAvailability? GetExportToolAvailability()
    {
        return exportToolAvailabilityService?.CheckAvailability();
    }

    private async Task<IReadOnlyList<LipSyncSegmentState>?> BuildLipSyncSegmentStatesAsync(
        Guid projectId,
        IReadOnlyList<TranslatedSegment> translatedSegments,
        CancellationToken cancellationToken)
    {
        if (lipSyncSegmentRepository is null)
            return null;

        IReadOnlyList<LipSyncSegment> lipSyncSegments = await lipSyncSegmentRepository
            .GetByProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        if (lipSyncSegments.Count == 0)
            return null;

        // LipSyncSegment.SegmentId is the TranslatedSegment.Id — join to get SegmentIndex.
        Dictionary<Guid, int> segmentIndexByTranslatedId = translatedSegments
            .ToDictionary(static s => s.Id, static s => s.SegmentIndex);

        // Keep only the most-recent segment per SegmentIndex.
        return lipSyncSegments
            .GroupBy(s => segmentIndexByTranslatedId.TryGetValue(s.SegmentId, out int idx) ? idx : -1)
            .Where(static g => g.Key >= 0)
            .Select(static g =>
            {
                LipSyncSegment latest = g.OrderByDescending(static s => s.CreatedAtUtc).First();
                return new LipSyncSegmentState(
                    SegmentIndex: g.Key,
                    Status: latest.Status,
                    AlignedTtsDuration: latest.AlignedTtsDuration,
                    PlanConfidence: latest.PlanConfidence,
                    SkipReason: latest.SkipReason,
                    FailureReason: latest.FailureReason,
                    ProviderId: latest.ProviderId,
                    ModelId: latest.ModelId);
            })
            .ToArray();
    }

    private async Task<IReadOnlyList<LipSynthesisSegmentUiState>?> BuildLipSynthesisSegmentUiStatesAsync(
        Guid projectId,
        IReadOnlyList<TranscriptSegment> transcriptSegments,
        IReadOnlyList<SpeakerTurn> speakerTurns,
        CancellationToken cancellationToken)
    {
        if (lipSynthesisSegmentRepository is null)
        {
            return null;
        }

        IReadOnlyList<Domain.LipSynthesis.LipSynthesisSegment> segments = await lipSynthesisSegmentRepository
            .GetByProjectAsync(projectId, cancellationToken)
            .ConfigureAwait(false);

        return LipSynthesisSegmentUiStateBuilder.Build(segments, transcriptSegments, speakerTurns);
    }

    private async Task<IReadOnlyList<ProjectSpeaker>> LoadOrCreateSpeakersAsync(
        Guid projectId,
        TranscriptRevision? currentRevision,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ProjectSpeaker> speakers = await speakerRepository.ListSpeakersAsync(
            projectId,
            cancellationToken).ConfigureAwait(false);
        if (speakers.Count > 0 || currentRevision is null)
        {
            return speakers;
        }

        ProjectSpeaker defaultSpeaker = await speakerRepository.EnsureDefaultSpeakerAsync(
            projectId,
            cancellationToken).ConfigureAwait(false);
        return [defaultSpeaker];
    }

    private Task<WaveformSummary?> ReadWaveformSummaryAsync(CancellationToken cancellationToken)
    {
        if (!artifactStore.Exists(ProjectArtifactPaths.WaveformSummaryRelativePath))
        {
            return Task.FromResult<WaveformSummary?>(null);
        }

        return artifactStore.ReadJsonAsync<WaveformSummary>(
            ProjectArtifactPaths.WaveformSummaryRelativePath,
            cancellationToken);
    }

    private readonly struct ProjectOpenReadProfile
    {
        public bool IncludeWaveformSummary { get; init; }
        public bool IncludeTtsCandidates { get; init; }
        public bool IncludeLipSyncStates { get; init; }
        public bool IncludeLipSynthesisStates { get; init; }
        public bool IncludeMixPlan { get; init; }

        public static ProjectOpenReadProfile Full { get; } = new()
        {
            IncludeWaveformSummary = true,
            IncludeTtsCandidates = true,
            IncludeLipSyncStates = true,
            IncludeLipSynthesisStates = true,
            IncludeMixPlan = true,
        };

        public static ProjectOpenReadProfile Shell { get; } = new()
        {
            IncludeWaveformSummary = false,
            IncludeTtsCandidates = false,
            IncludeLipSyncStates = false,
            IncludeLipSynthesisStates = false,
            IncludeMixPlan = false,
        };
    }
}
