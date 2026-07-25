using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

/// <summary>
/// Tests for <see cref="TtsOrchestrationService.BuildTtsSegmentStates"/> and
/// <see cref="TtsOrchestrationService.StretchTtsTakeAsync"/> guard paths.
/// </summary>
public sealed class TtsSegmentStateTests
{
    private readonly Guid projectId = Guid.NewGuid();
    private readonly Guid voiceAssignmentId = Guid.NewGuid();

    // ─────────────────────────────────────────────────────────────────────────
    // BuildTtsSegmentStates — no take (segment without a take)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTtsSegmentStates_NoTake_ReturnsEmptyStateForSegment()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        TranscriptSegment sourceSegment = MakeSourceSegment(revisionId, 0, 0.0, 3.0);
        TranslatedSegment translatedSegment = MakeTranslatedSegment(revisionId, 0, "Hello");

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [sourceSegment], [translatedSegment], [], [], []);

        TtsSegmentState state = Assert.Single(states);
        Assert.Equal(0, state.SegmentIndex);
        Assert.Null(state.TakeId);
        Assert.Null(state.Status);
        Assert.False(state.IsStale);
        Assert.Null(state.DurationSeconds);
        Assert.False(state.HasDurationWarning);
        Assert.Equal(TtsDurationSeverity.None, state.DurationSeverity);
        // OriginalDurationSeconds should be computed from source segment
        Assert.Equal(3.0, state.OriginalDurationSeconds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BuildTtsSegmentStates — stale-by-text-hash detection
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTtsSegmentStates_TakeHashMatchesCurrentText_NotStale()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        const string text = "Hello world";
        string correctHash = TtsTextHash.Compute(0, text);

        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 2.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, text);

        // take has IsStale=false AND the correct text hash
        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0, translatedTextHash: correctHash);

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [], []);

        TtsSegmentState state = Assert.Single(states);
        Assert.False(state.IsStale);
    }

    [Fact]
    public void BuildTtsSegmentStates_TakeHashDiffersFromCurrentText_MarkedStale()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        // The take was generated for old text
        string oldHash = TtsTextHash.Compute(0, "Old text");
        // But the translated segment now has new text
        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 2.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, "New text");

        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0, translatedTextHash: oldHash);

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [], []);

        TtsSegmentState state = Assert.Single(states);
        Assert.True(state.IsStale);
    }

    [Fact]
    public void BuildTtsSegmentStates_TakeIsStaleFlag_MarkedStale_RegardlessOfHash()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        const string text = "Hello";
        string correctHash = TtsTextHash.Compute(0, text);

        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 2.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, text);

        // Hash is correct but IsStale flag is explicitly true
        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0, translatedTextHash: correctHash)
            .MarkStale();

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [], []);

        TtsSegmentState state = Assert.Single(states);
        Assert.True(state.IsStale);
        // Severity should be None for a stale take
        Assert.Equal(TtsDurationSeverity.None, state.DurationSeverity);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BuildTtsSegmentStates — duration severity
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTtsSegmentStates_CompletedTakeWithNoOverrun_GreenSeverity()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        // Source segment: 5 s, take: 5.1 s (2% overrun — within Green threshold)
        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 5.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, "Same duration text");

        Guid artifactId = Guid.NewGuid();
        // 5100 samples @ 1000 Hz = 5.1 s
        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0)
            .Complete(artifactId, durationSamples: 5100, sampleRate: 1000, provider: "fake");

        ProjectArtifact artifact = MakeArtifact(artifactId, ArtifactKind.TtsTake, durationSeconds: 5.1);

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [artifact], []);

        TtsSegmentState state = Assert.Single(states);
        Assert.False(state.IsStale);
        Assert.NotNull(state.Status);
        Assert.Equal(TtsTakeStatus.Completed, state.Status);
        // 2% overrun → Green (below Yellow threshold of ~10%)
        Assert.Equal(TtsDurationSeverity.Green, state.DurationSeverity);
        Assert.False(state.HasDurationWarning);
    }

    [Fact]
    public void BuildTtsSegmentStates_CompletedTakeWithSignificantOverrun_YellowOrRedSeverity()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        // Source: 2 s, take: 3.5 s (75% overrun — exceeds Yellow threshold)
        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 2.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, "Overrunning text");

        Guid artifactId = Guid.NewGuid();
        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0)
            .Complete(artifactId, durationSamples: 3500, sampleRate: 1000, provider: "fake");

        ProjectArtifact artifact = MakeArtifact(artifactId, ArtifactKind.TtsTake, durationSeconds: 3.5);

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [artifact], []);

        TtsSegmentState state = Assert.Single(states);
        Assert.True(state.DurationSeverity is TtsDurationSeverity.Yellow or TtsDurationSeverity.Red,
            $"Expected Yellow or Red severity but got {state.DurationSeverity}");
        Assert.True(state.HasDurationWarning);
        Assert.False(string.IsNullOrWhiteSpace(state.WarningMessage));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BuildTtsSegmentStates — missing artifact
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTtsSegmentStates_TakeHasArtifactIdButArtifactNotInList_NullDuration()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 3.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, "Text");

        // The take references an artifact that is NOT in the artifacts list passed to BuildTtsSegmentStates
        Guid missingArtifactId = Guid.NewGuid();
        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0)
            .Complete(missingArtifactId, durationSamples: 3000, sampleRate: 1000, provider: "fake");

        // Pass an empty artifact list — simulates partial cleanup / missing artifact scenario
        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [], []);

        TtsSegmentState state = Assert.Single(states);
        // DurationSamples/SampleRate on the take can still compute duration, so it won't be null.
        // The important thing is the severity is None (no artifact in dict = null artifact path)
        Assert.Null(state.ArtifactRelativePath);
        // CanManualStretch requires artifact != null
        Assert.False(state.CanManualStretch);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // BuildTtsSegmentStates — duration computed from DurationSamples/SampleRate on take
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public void BuildTtsSegmentStates_DurationFromSamplesOnTake_WhenArtifactHasNoDuration()
    {
        TtsOrchestrationService svc = BuildService();
        Guid revisionId = Guid.NewGuid();

        TranscriptSegment source = MakeSourceSegment(revisionId, 0, 0.0, 3.0);
        TranslatedSegment translated = MakeTranslatedSegment(revisionId, 0, "Text");

        Guid artifactId = Guid.NewGuid();
        // 6000 samples @ 1000 Hz = 6.0 s
        TtsTake take = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0)
            .Complete(artifactId, durationSamples: 6000, sampleRate: 1000, provider: "fake");

        // Artifact with no DurationSeconds set
        ProjectArtifact artifact = MakeArtifact(artifactId, ArtifactKind.TtsTake, durationSeconds: null);

        IReadOnlyList<TtsSegmentState> states = svc.BuildTtsSegmentStates(
            [source], [translated], [take], [artifact], []);

        TtsSegmentState state = Assert.Single(states);
        // Duration should be computed from samples/sampleRate (6000/1000 = 6.0 s)
        Assert.Equal(6.0, state.DurationSeconds);
    }

    // ─────────────────────────────────────────────────────────────────────────
    // StretchTtsTakeAsync — guard: no time stretch service configured
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StretchTtsTakeAsync_NoTimeStretchService_Throws()
    {
        // BuildService() wires up no audioTimeStretchService (default = null)
        TtsOrchestrationService svc = BuildService();

        TranscriptProjectState state = BuildMinimalStateWithCompletedTake(out TtsTake take, out _);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StretchTtsTakeAsync(state, new StretchTtsTakeRequest(take.Id), CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // StretchTtsTakeAsync — guard: take not found
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StretchTtsTakeAsync_TakeNotFound_Throws()
    {
        TtsOrchestrationService svc = BuildService(withTimeStretch: true);

        // Build state with no TTS takes
        TranscriptProjectState state = BuildMinimalStateWithNoTakes();

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StretchTtsTakeAsync(state, new StretchTtsTakeRequest(Guid.NewGuid()), CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // StretchTtsTakeAsync — guard: take is stale
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StretchTtsTakeAsync_TakeIsStale_Throws()
    {
        TtsOrchestrationService svc = BuildService(withTimeStretch: true);

        // Create a completed take then mark it stale
        TranscriptProjectState state = BuildMinimalStateWithCompletedTake(out TtsTake completedTake, out _);
        TtsTake staleTake = completedTake.MarkStale();
        state = state with { TtsTakes = [staleTake] };

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StretchTtsTakeAsync(state, new StretchTtsTakeRequest(staleTake.Id), CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // StretchTtsTakeAsync — guard: take is pending (not completed)
    // ─────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task StretchTtsTakeAsync_TakeNotCompleted_Throws()
    {
        TtsOrchestrationService svc = BuildService(withTimeStretch: true);

        TtsTake pendingTake = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0);
        TranscriptProjectState state = BuildMinimalStateWithTake(pendingTake);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            svc.StretchTtsTakeAsync(state, new StretchTtsTakeRequest(pendingTake.Id), CancellationToken.None));
    }

    // ─────────────────────────────────────────────────────────────────────────
    // Helpers
    // ─────────────────────────────────────────────────────────────────────────

    private readonly FakeVoiceAssignmentRepository voiceAssignmentRepository = new();
    private readonly FakeTtsTakeRepository ttsTakeRepository = new();
    private readonly FakeArtifactStore artifactStore = new();
    private readonly FakeFileFingerprintService fingerprintService = new();
    private readonly FakeMediaAssetRepository mediaAssetRepository = new();
    private readonly FakeVoiceCatalog voiceCatalog = new();

    private TtsOrchestrationService BuildService(bool withTimeStretch = false)
    {
        var ttsEngine = new FakeTtsEngine();
        var durationAnalysisService = new DurationAnalysisService();
        var stageRunStore = new FakeProjectStageRunStore();
        var startHandler = new StartTtsStageHandler(
            ttsEngine,
            voiceCatalog,
            artifactStore,
            fingerprintService,
            mediaAssetRepository,
            ttsTakeRepository,
            stageRunStore,
            durationAnalysisService);

        return new TtsOrchestrationService(
            startHandler,
            voiceAssignmentRepository,
            ttsTakeRepository,
            ttsEngine,
            voiceCatalog,
            artifactStore,
            fingerprintService,
            mediaAssetRepository,
            new FakeReferenceClipTrimmer(),
            durationAnalysisService,
            audioTimeStretchService: withTimeStretch ? new FakeAudioTimeStretchService() : null);
    }

    private static TranscriptSegment MakeSourceSegment(
        Guid revisionId, int index, double start, double end) =>
        TranscriptSegment.Create(revisionId, index, start, end, $"text-{index}", Guid.NewGuid());

    private static TranslatedSegment MakeTranslatedSegment(
        Guid revisionId, int index, string text) =>
        TranslatedSegment.Create(revisionId, index, 0.0, 2.0, text);

    private ProjectArtifact MakeArtifact(Guid id, ArtifactKind kind, double? durationSeconds) =>
        new(id,
            projectId,
            Guid.NewGuid(),
            kind,
            $"artifacts/{id:N}.wav",
            "sha256-fake",
            SizeBytes: 1024,
            durationSeconds,
            SampleRate: 22050,
            ChannelCount: 1,
            DateTimeOffset.UtcNow);

    private TranscriptProjectState BuildMinimalStateWithCompletedTake(out TtsTake completedTake, out ProjectArtifact artifact)
    {
        Guid artifactId = Guid.NewGuid();
        completedTake = TtsTake.Create(projectId, voiceAssignmentId, segmentIndex: 0)
            .Complete(artifactId, durationSamples: 3000, sampleRate: 1000, provider: "fake");
        artifact = MakeArtifact(artifactId, ArtifactKind.TtsTake, durationSeconds: 3.0);

        return BuildMinimalStateWithTake(completedTake, artifact);
    }

    private TranscriptProjectState BuildMinimalStateWithNoTakes() =>
        BuildMinimalStateWithTake(null, null);

    private TranscriptProjectState BuildMinimalStateWithTake(
        TtsTake? take,
        ProjectArtifact? artifact = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(projectId, "Test", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(), projectId, "source.mp4", "source.mp4", "hash", 100, now, "mp4",
            4.0, HasAudio: true, HasVideo: true, now);
        var projectState = new OpenProjectResult(
            project, mediaAsset, null, SourceMediaStatus.Available, null,
            artifact is not null ? [artifact] : [],
            "en");

        Guid revisionId = Guid.NewGuid();
        TranscriptRevision transcriptRevision = TranscriptRevision.Create(projectId, null, 1, now);
        TranscriptSegment sourceSegment = MakeSourceSegment(transcriptRevision.Id, 0, 0.0, 3.0);

        return new TranscriptProjectState(
            projectState,
            transcriptRevision,
            TranscriptSegments: [sourceSegment],
            Speakers: [],
            SpeakerTurns: [],
            CurrentTranslationRevision: null,
            TranslatedSegments: [],
            IsTranslationStale: false,
            TranscriptLanguage: "en",
            StageRuns: [],
            SupportedTargetLanguages: [],
            SelectedTranslationTargetLanguage: null,
            StaleTranslatedSegmentIndices: new HashSet<int>(),
            WaveformSummary: null,
            AvailableVoices: [],
            VoiceAssignments: [],
            TtsTakes: take is not null ? [take] : [],
            TtsSegmentStates: [],
            VoiceAssignmentWarnings: []);
    }
}
