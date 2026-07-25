using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class OrchestrationServiceTests
{
    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_persists_fake_engine_output()
    {
        FakeTranslationEngine translationEngine = new(
            static (request, segment) => $"{request.TargetLanguage}:{segment.Index}:{segment.Text}",
            static _ => new TranslationExecutionMetadata(
                "fake-runtime-provider",
                "fake-runtime-model",
                "fake-alias",
                "cpu",
                TranslationRoutingKind.Direct));
        TranslationServiceContext context = CreateTranslationServiceContext(translationEngine);
        TranscriptProjectState state = CreateTranscriptProjectState();

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        Assert.Equal(1, revision.RevisionNumber);
        Assert.Equal("es", revision.TargetLanguage);
        Assert.Equal("fake-runtime-provider", revision.TranslationProvider);
        Assert.Equal("fake-runtime-model", revision.ModelId);
        Assert.Equal("cpu", revision.ExecutionProvider);

        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
        Assert.Collection(
            segments,
            segment =>
            {
                Assert.Equal(0, segment.SegmentIndex);
                Assert.Equal("es:0:Hello there.", segment.Text);
                Assert.False(string.IsNullOrWhiteSpace(segment.SourceSegmentHash));
            },
            segment =>
            {
                Assert.Equal(1, segment.SegmentIndex);
                Assert.Equal("es:1:Second line.", segment.Text);
                Assert.False(string.IsNullOrWhiteSpace(segment.SourceSegmentHash));
            });

        StageRunRecord stageRun = Assert.Single(context.StageRunStore.All);
        Assert.Equal(StageNames.Translation, stageRun.StageName);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
        Assert.Equal(stageRun.Id, revision.StageRunId);

        ProjectArtifact artifact = Assert.Single(
            context.MediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.TranslationRevision);
        Assert.True(context.ArtifactStore.Exists(artifact.RelativePath));
    }

    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_uses_translated_word_alignment_when_available()
    {
        var translationEngine = new FakeTranslationEngine((_, segment) =>
            segment.Index == 0 ? "hola mundo" : "segunda linea");
        var aligner = new FakeTranslatedWordAlignmentService(request =>
            TranslatedWordAlignmentResult.Succeeded(
            [
                TranslatedWord.Create(0, request.TranslatedSegment.StartSeconds, request.TranslatedSegment.StartSeconds + 0.8d, request.TranslatedSegment.Text.Split(' ')[0]),
                TranslatedWord.Create(1, request.TranslatedSegment.StartSeconds + 0.8d, request.TranslatedSegment.EndSeconds, request.TranslatedSegment.Text.Split(' ')[1])
            ]));
        TranslationServiceContext context = CreateTranslationServiceContext(
            translationEngine,
            translatedWordAlignmentService: aligner);
        TranscriptProjectState state = CreateTranscriptProjectState();

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);

        Assert.All(segments, segment => Assert.Equal(2, segment.Words.Count));
        Assert.Equal("hola", segments[0].Words[0].Text);
        Assert.Equal("mundo", segments[0].Words[1].Text);
    }

    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_leaves_words_empty_when_alignment_is_unavailable()
    {
        var translationEngine = new FakeTranslationEngine((_, segment) =>
            segment.Index == 0 ? "hola mundo" : "segunda linea");
        var aligner = new FakeTranslatedWordAlignmentService(_ =>
            TranslatedWordAlignmentResult.Unavailable("aligner missing"));
        TranslationServiceContext context = CreateTranslationServiceContext(
            translationEngine,
            translatedWordAlignmentService: aligner);
        TranscriptProjectState state = CreateTranscriptProjectState();

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);

        Assert.All(segments, segment => Assert.Empty(segment.Words));
    }

    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_rejects_invalid_aligned_words()
    {
        var translationEngine = new FakeTranslationEngine((_, segment) =>
            segment.Index == 0 ? "hola mundo" : "segunda linea");
        var aligner = new FakeTranslatedWordAlignmentService(request =>
            TranslatedWordAlignmentResult.Succeeded(
            [
                TranslatedWord.Create(0, request.TranslatedSegment.StartSeconds, request.TranslatedSegment.EndSeconds, "adios")
            ]));
        TranslationServiceContext context = CreateTranslationServiceContext(
            translationEngine,
            translatedWordAlignmentService: aligner);
        TranscriptProjectState state = CreateTranscriptProjectState();

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);

        Assert.All(segments, segment => Assert.Empty(segment.Words));
    }

    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_passes_project_glossary_hints()
    {
        var translationEngine = new FakeTranslationEngine(static (_, segment) => segment.Text);
        var glossaryRepository = new FakeGlossaryRepository();
        TranslationServiceContext context = CreateTranslationServiceContext(translationEngine, glossaryRepository);
        TranscriptProjectState state = CreateTranscriptProjectState();
        await glossaryRepository.SaveAsync(
            GlossaryEntry.Create(
                state.ProjectState.Project.Id,
                "en",
                "es",
                "Hello",
                "Hola",
                isCaseSensitive: false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Hola there.", segments[0].Text);
    }

    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_passes_only_matched_cjk_glossary_hints_with_spans()
    {
        IReadOnlyList<TranslationGlossaryHint>? observedHints = null;
        var translationEngine = new FakeTranslationEngine((request, segment) =>
        {
            observedHints = request.GlossaryHints;
            return segment.Text;
        });
        var glossaryRepository = new FakeGlossaryRepository();
        var translationRouter = new FakeTranslationLanguageRouter();
        translationRouter.SetSupportedTargetLanguages(
            "ja",
            new TranslationTargetLanguageOption("en", "English", TranslationRoutingKind.Direct, IsAvailable: true, "Direct fake route"));
        translationRouter.SetRoute(new TranslationRouteSelection(
            "ja",
            "en",
            TranslationRoutingKind.Direct,
            IsAvailable: true,
            "fake",
            "Direct fake route",
            "fake-ja-en",
            "fake-ja-en",
            EngineFamily: "fake"));
        TranslationServiceContext context = CreateTranslationServiceContext(
            translationEngine,
            glossaryRepository,
            translationRouter);
        TranscriptProjectState state = CreateTranscriptProjectState("ja");
        state = state with
        {
            TranscriptSegments =
            [
                TranscriptSegment.Create(
                    state.CurrentTranscriptRevision!.Id,
                    0,
                    0.0d,
                    2.0d,
                    "星宮先輩",
                    state.Speakers[0].Id,
                    "ja")
            ]
        };
        await glossaryRepository.SaveAsync(
            GlossaryEntry.Create(
                state.ProjectState.Project.Id,
                "ja",
                "en",
                "星宮",
                "Hoshimiya",
                isCaseSensitive: false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        await glossaryRepository.SaveAsync(
            GlossaryEntry.Create(
                state.ProjectState.Project.Id,
                "ja",
                "en",
                "未登場",
                "unseen",
                isCaseSensitive: false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("ja", "en"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Hoshimiya先輩", Assert.Single(segments).Text);

        TranslationGlossaryHint hint = Assert.Single(observedHints ?? []);
        Assert.Equal("星宮", hint.SourceTerm);
        TranslationGlossarySourceMatch match = Assert.Single(hint.SourceMatches ?? []);
        Assert.Equal(0, match.SegmentIndex);
        Assert.Equal(0, match.StartTextElementIndex);
        Assert.Equal(2, match.TextElementLength);
        Assert.Equal("星宮", match.MatchedSourceTerm);
    }

    [Fact]
    public async Task TranslationOrchestrationService_GenerateTranslationAsync_passes_only_normalized_latin_matches_with_spans()
    {
        IReadOnlyList<TranslationGlossaryHint>? observedHints = null;
        var translationEngine = new FakeTranslationEngine((request, segment) =>
        {
            observedHints = request.GlossaryHints;
            return segment.Text;
        });
        var glossaryRepository = new FakeGlossaryRepository();
        var translationRouter = new FakeTranslationLanguageRouter();
        translationRouter.SetSupportedTargetLanguages(
            "fr",
            new TranslationTargetLanguageOption("en", "English", TranslationRoutingKind.Direct, IsAvailable: true, "Direct fake route"));
        translationRouter.SetRoute(new TranslationRouteSelection(
            "fr",
            "en",
            TranslationRoutingKind.Direct,
            IsAvailable: true,
            "fake",
            "Direct fake route",
            "fake-fr-en",
            "fake-fr-en",
            EngineFamily: "fake"));
        TranslationServiceContext context = CreateTranslationServiceContext(
            translationEngine,
            glossaryRepository,
            translationRouter);
        TranscriptProjectState state = CreateTranscriptProjectState("fr");
        state = state with
        {
            TranscriptSegments =
            [
                TranscriptSegment.Create(
                    state.CurrentTranscriptRevision!.Id,
                    0,
                    0.0d,
                    2.0d,
                    "Le Café ouvre.",
                    state.Speakers[0].Id,
                    "fr")
            ]
        };
        await glossaryRepository.SaveAsync(
            GlossaryEntry.Create(
                state.ProjectState.Project.Id,
                "fr",
                "en",
                "cafe",
                "cafe glossary",
                isCaseSensitive: false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);
        await glossaryRepository.SaveAsync(
            GlossaryEntry.Create(
                state.ProjectState.Project.Id,
                "fr",
                "en",
                "absent",
                "absent glossary",
                isCaseSensitive: false,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await context.Service.GenerateTranslationAsync(
            state,
            new GenerateTranslationRequest("fr", "en"),
            TestContext.Current.CancellationToken);

        TranslationRevision revision = Assert.Single(context.TranslationRepository.Revisions);
        IReadOnlyList<TranslatedSegment> segments = await context.TranslationRepository
            .GetSegmentsAsync(revision.Id, TestContext.Current.CancellationToken);
        Assert.Equal("Le cafe glossary ouvre.", Assert.Single(segments).Text);

        TranslationGlossaryHint hint = Assert.Single(observedHints ?? []);
        Assert.Equal("cafe", hint.SourceTerm);
        TranslationGlossarySourceMatch match = Assert.Single(hint.SourceMatches ?? []);
        Assert.Equal(0, match.SegmentIndex);
        Assert.Equal(3, match.StartTextElementIndex);
        Assert.Equal(4, match.TextElementLength);
        Assert.Equal("Café", match.MatchedSourceTerm);
    }

    [Fact]
    public async Task TranslationOrchestrationService_SaveTranslationEditsAsync_clears_changed_segment_words_and_preserves_unchanged_segments()
    {
        TranslationServiceContext context = CreateTranslationServiceContext(new FakeTranslationEngine());
        TranscriptProjectState currentState = CreateTranslatedProjectStateWithWords();

        context.TranslationRepository.Seed(currentState.CurrentTranslationRevision!, currentState.TranslatedSegments);

        await context.Service.SaveTranslationEditsAsync(
            currentState,
            new SaveTranslationEditsRequest(
                currentState.CurrentTranslationRevision!.Id,
                "es",
                [new EditedTranslatedSegment(0, "Linea editada.")]),
            TestContext.Current.CancellationToken);

        TranslationRevision savedRevision = Assert.Single(context.TranslationRepository.Revisions, revision => revision.RevisionNumber == 2);
        IReadOnlyList<TranslatedSegment> savedSegments = await context.TranslationRepository
            .GetSegmentsAsync(savedRevision.Id, TestContext.Current.CancellationToken);

        Assert.Collection(
            savedSegments.OrderBy(static segment => segment.SegmentIndex),
            changedSegment =>
            {
                Assert.Equal("Linea editada.", changedSegment.Text);
                Assert.Empty(changedSegment.Words);
            },
            unchangedSegment =>
            {
                Assert.Equal("Segunda linea.", unchangedSegment.Text);
                Assert.Collection(
                    unchangedSegment.Words,
                    first => Assert.Equal("Segunda", first.Text),
                    second => Assert.Equal("linea", second.Text));
            });
    }

    [Fact]
    public async Task TranslationOrchestrationService_RetranslateSegmentAsync_preserves_other_segment_words_and_clears_replaced_segment_words()
    {
        var translationEngine = new FakeTranslationEngine((_, segment) =>
            segment.Index == 0
                ? "Linea re-generada."
                : segment.Text);
        TranslationServiceContext context = CreateTranslationServiceContext(translationEngine);
        TranscriptProjectState currentState = CreateTranslatedProjectStateWithWords();

        context.TranslationRepository.Seed(currentState.CurrentTranslationRevision!, currentState.TranslatedSegments);

        await context.Service.RetranslateSegmentAsync(
            currentState,
            new RetranslateSegmentRequest(
                currentState.CurrentTranslationRevision!.Id,
                currentState.TranscriptSegments[0].Id,
                "en",
                "es"),
            TestContext.Current.CancellationToken);

        TranslationRevision savedRevision = Assert.Single(context.TranslationRepository.Revisions, revision => revision.RevisionNumber == 2);
        IReadOnlyList<TranslatedSegment> savedSegments = await context.TranslationRepository
            .GetSegmentsAsync(savedRevision.Id, TestContext.Current.CancellationToken);

        Assert.Collection(
            savedSegments.OrderBy(static segment => segment.SegmentIndex),
            changedSegment =>
            {
                Assert.Equal("Linea re-generada.", changedSegment.Text);
                Assert.Empty(changedSegment.Words);
            },
            unchangedSegment =>
            {
                Assert.Equal("Segunda linea.", unchangedSegment.Text);
                Assert.Collection(
                    unchangedSegment.Words,
                    first => Assert.Equal("Segunda", first.Text),
                    second => Assert.Equal("linea", second.Text));
            });
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_persists_fake_engine_take()
    {
        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 1000 };
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(1, ttsEngine.SynthesizeCallCount);
        Assert.Equal("Linea traducida.", ttsEngine.LastInputText);
        Assert.Equal("af_heart", ttsEngine.LastVoicepack?.VoiceId);
        Assert.Equal("kokoro-onnx", ttsEngine.LastOptions?.PreferredModelAlias);
        Assert.True(ttsEngine.LastOptions?.RequirePreferredModelAlias);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeStatus.Completed, take.Status);
        Assert.Equal(0, take.SegmentIndex);
        Assert.Equal("fake", take.Provider);
        Assert.Equal("fake", take.ModelId);
        Assert.Equal("af_heart", take.VoiceId);
        Assert.Equal(TtsTakeKind.Stock, take.Kind);
        Assert.Null(take.ReferenceClipArtifactId);
        Assert.Equal(1000, take.DurationSamples);
        Assert.Equal(1000, take.SampleRate);

        ProjectArtifact artifact = Assert.Single(
            context.MediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.TtsTake);
        Assert.Equal(artifact.Id, take.ArtifactId);
        Assert.True(context.ArtifactStore.Exists(artifact.RelativePath));

        StageRunRecord stageRun = Assert.Single(context.StageRunStore.All);
        Assert.Equal(StageNames.Tts, stageRun.StageName);
        Assert.Equal(StageRunStatus.Completed, stageRun.Status);
        Assert.Equal(stageRun.Id, take.StageRunId);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_skips_orphaned_artifact_path()
    {
        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 1000 };
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid speakerId = state.Speakers[0].Id;
        Guid translatedSegmentId = state.TranslatedSegments[0].Id;
        string orphanedPath = ProjectArtifactPaths.GetTtsTakeRelativePath(
            speakerId,
            translatedSegmentId,
            takeNumber: 1);
        await context.MediaAssetRepository.SaveArtifactAsync(
            new ProjectArtifact(
                Guid.NewGuid(),
                state.ProjectState.Project.Id,
                state.ProjectState.MediaAsset!.Id,
                ArtifactKind.TtsTake,
                orphanedPath,
                "orphaned-sha",
                4,
                1.0d,
                1000,
                1,
                DateTimeOffset.UtcNow),
            TestContext.Current.CancellationToken);

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        ProjectArtifact artifact = Assert.Single(
            context.MediaAssetRepository.Artifacts,
            artifact => artifact.Id == take.ArtifactId);
        Assert.Equal(
            ProjectArtifactPaths.GetTtsTakeRelativePath(speakerId, translatedSegmentId, takeNumber: 2),
            artifact.RelativePath);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_requires_voice_clone_consent()
    {
        var consent = new FakeConsentService();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent);
        var auditLog = new FakeAuditLog();
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            auditLog,
            new FakeReferenceClipAnalyzer(),
            workspace.Root);
        (TranscriptProjectState state, Guid speakerId, _) = CreateVoiceClonedTranslatedProjectState(context.ArtifactStore);
        state = AddNormalizedAudioArtifact(state, context.ArtifactStore);

        await Assert.ThrowsAsync<ConsentRequiredException>(() =>
            context.Service.GenerateTtsForSpeakerAsync(
                state,
                new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
                TestContext.Current.CancellationToken));

        Assert.Equal(0, ttsEngine.SynthesizeCallCount);
        Assert.Empty(auditLog.Entries);
        Assert.Empty(context.TtsTakeRepository.All);
        Assert.DoesNotContain(context.MediaAssetRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.TtsTake);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_records_cloned_take_and_audit_entry()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var auditLog = new FakeAuditLog();
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            auditLog,
            new FakeReferenceClipAnalyzer(),
            workspace.Root);
        (TranscriptProjectState state, Guid speakerId, Guid referenceClipArtifactId) =
            CreateVoiceClonedTranslatedProjectState(context.ArtifactStore);

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeKind.VoiceCloned, take.Kind);
        Assert.Equal(referenceClipArtifactId, take.ReferenceClipArtifactId);
        Assert.Equal(referenceClipArtifactId, ttsEngine.LastReferenceClipArtifactId);
        Assert.Equal(2.0d, ttsEngine.LastRequest?.TargetDurationSeconds);

        VoiceCloneAuditEntry entry = Assert.Single(auditLog.Entries);
        Assert.Equal(speakerId, entry.SpeakerId);
        Assert.Equal(referenceClipArtifactId, entry.ReferenceClipArtifactId);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_auto_captures_reference_for_clone_without_reference_clip()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var auditLog = new FakeAuditLog();
        var analyzer = new FakeReferenceClipAnalyzer
        {
            Analysis = new ReferenceClipAnalysis(
                TotalDurationSeconds: 4d,
                ActiveSpeechSeconds: 4d,
                SampleRate: 24000,
                ChannelCount: 1)
        };
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            auditLog,
            analyzer,
            workspace.Root);
        TranscriptProjectState state = AddNormalizedAudioArtifact(
            CreateTranslatedProjectState(),
            context.ArtifactStore);
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeKind.VoiceCloned, take.Kind);
        Assert.NotNull(take.ReferenceClipArtifactId);
        Assert.Equal(take.ReferenceClipArtifactId, ttsEngine.LastReferenceClipArtifactId);
        ProjectArtifact referenceArtifact = Assert.Single(
            context.MediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.ReferenceClip);
        Assert.Equal(take.ReferenceClipArtifactId, referenceArtifact.Id);
        Assert.Contains("auto-speaker-reference:v2", referenceArtifact.Provenance);
        Assert.Contains("mode:single", referenceArtifact.Provenance);
        Assert.Equal(1, context.ReferenceClipTrimmer.TrimCallCount);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForAllSpeakersAsync_auto_captures_clone_without_voice_assignment()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var analyzer = new FakeReferenceClipAnalyzer
        {
            Analysis = new ReferenceClipAnalysis(
                TotalDurationSeconds: 4d,
                ActiveSpeechSeconds: 4d,
                SampleRate: 24000,
                ChannelCount: 1)
        };
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            new FakeAuditLog(),
            analyzer,
            workspace.Root);
        TranscriptProjectState state = AddNormalizedAudioArtifact(
            CreateTranslatedProjectState() with { VoiceAssignments = [] },
            context.ArtifactStore);
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForAllSpeakersAsync(
            state,
            new GenerateTtsForAllSpeakersRequest(
                UseReferenceClipForVoiceCloningBySpeakerId: new Dictionary<Guid, bool>
                {
                    [speakerId] = true
                }),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeKind.VoiceCloned, take.Kind);
        Assert.NotNull(take.ReferenceClipArtifactId);
        VoiceAssignment assignment = Assert.Single(context.VoiceAssignmentRepository.All);
        Assert.Equal(VoiceCloningDefaults.ChatterboxPrimaryAlias, assignment.VoiceModelId);
        Assert.True(assignment.RequiresConsent);
        Assert.False(assignment.IsFallback);
        Assert.Equal(take.ReferenceClipArtifactId, assignment.ReferenceClipArtifactId);
        Assert.Equal(take.ReferenceClipArtifactId, ttsEngine.LastReferenceClipArtifactId);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_packs_segments_when_single_auto_reference_is_too_short()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var analyzer = new FakeReferenceClipAnalyzer();
        analyzer.QueueAnalysis(new ReferenceClipAnalysis(2d, 2d, 24000, 1));
        analyzer.QueueAnalysis(new ReferenceClipAnalysis(4d, 4d, 24000, 1));
        var clipExtractor = new FakeAudioClipExtractor();
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            new FakeAuditLog(),
            analyzer,
            workspace.Root,
            clipExtractor);
        TranscriptProjectState state = AddNormalizedAudioArtifact(
            CreateTranslatedProjectState(),
            context.ArtifactStore);
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        Assert.Collection(
            clipExtractor.LastRanges,
            range =>
            {
                Assert.Equal(0d, range.StartSeconds);
                Assert.Equal(2d, range.EndSeconds);
            },
            range =>
            {
                Assert.Equal(2d, range.StartSeconds);
                Assert.Equal(4d, range.EndSeconds);
            });
        ProjectArtifact referenceArtifact = Assert.Single(
            context.MediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.ReferenceClip);
        Assert.Contains("mode:packed", referenceArtifact.Provenance);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_uses_transcript_assignment_for_auto_reference_over_diarization_turn()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var clipExtractor = new FakeAudioClipExtractor();
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            new FakeAuditLog(),
            new FakeReferenceClipAnalyzer(),
            workspace.Root,
            clipExtractor);
        TranscriptProjectState state = AddNormalizedAudioArtifact(
            CreateTranslatedProjectState(),
            context.ArtifactStore);
        Guid speakerId = state.Speakers[0].Id;
        TranscriptRevision transcriptRevision = state.CurrentTranscriptRevision!;
        TranscriptSegment correctedSegment = TranscriptSegment.Create(
            transcriptRevision.Id,
            0,
            1d,
            3d,
            "Corrected speaker line.",
            speakerId,
            "en");
        state = state with
        {
            TranscriptSegments = [correctedSegment],
            SpeakerTurns =
            [
                SpeakerTurn.Create(
                    state.ProjectState.Project.Id,
                    speakerId,
                    0d,
                    4d,
                    confidence: 0.99d)
            ]
        };

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        AudioClipRange range = Assert.Single(clipExtractor.LastRanges);
        Assert.Equal(1d, range.StartSeconds);
        Assert.Equal(3d, range.EndSeconds);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_preserves_uploaded_reference_clip()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            new FakeAuditLog(),
            new FakeReferenceClipAnalyzer(),
            workspace.Root);
        TranscriptProjectState state = AddNormalizedAudioArtifact(
            CreateTranslatedProjectState(),
            context.ArtifactStore);
        Guid speakerId = state.Speakers[0].Id;
        Guid manualReferenceArtifactId = Guid.NewGuid();
        string relativePath = $"artifacts/reference-clips/{speakerId:D}/manual.wav";
        context.ArtifactStore.Seed(relativePath, [1, 2, 3, 4]);
        ProjectArtifact manualReference = new(
            manualReferenceArtifactId,
            state.ProjectState.Project.Id,
            state.ProjectState.MediaAsset!.Id,
            ArtifactKind.ReferenceClip,
            relativePath,
            "manual-sha",
            4,
            4d,
            24000,
            1,
            DateTimeOffset.UtcNow,
            Provenance: $"manual-speaker-reference:{speakerId:D};active-speech:4.000");
        state = state with
        {
            ProjectState = state.ProjectState with
            {
                Artifacts = state.ProjectState.Artifacts.Concat([manualReference]).ToArray()
            },
            VoiceAssignments =
            [
                state.VoiceAssignments[0] with
                {
                    ReferenceClipArtifactId = manualReferenceArtifactId,
                    RequiresConsent = true
                }
            ]
        };

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeKind.VoiceCloned, take.Kind);
        Assert.Equal(manualReferenceArtifactId, take.ReferenceClipArtifactId);
        Assert.DoesNotContain(context.MediaAssetRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.ReferenceClip);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_refreshes_stale_auto_reference_clip()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            new FakeAuditLog(),
            new FakeReferenceClipAnalyzer(),
            workspace.Root);
        TranscriptProjectState state = AddNormalizedAudioArtifact(
            CreateTranslatedProjectState(),
            context.ArtifactStore);
        Guid speakerId = state.Speakers[0].Id;
        Guid staleReferenceArtifactId = Guid.NewGuid();
        string relativePath = $"artifacts/reference-clips/{speakerId:D}/stale.wav";
        context.ArtifactStore.Seed(relativePath, [1, 2, 3, 4]);
        ProjectArtifact staleReference = new(
            staleReferenceArtifactId,
            state.ProjectState.Project.Id,
            state.ProjectState.MediaAsset!.Id,
            ArtifactKind.ReferenceClip,
            relativePath,
            "stale-sha",
            4,
            4d,
            24000,
            1,
            DateTimeOffset.UtcNow,
            Provenance: $"auto-speaker-reference:v1;speaker:{speakerId:D};fingerprint:old");
        state = state with
        {
            ProjectState = state.ProjectState with
            {
                Artifacts = state.ProjectState.Artifacts.Concat([staleReference]).ToArray()
            },
            VoiceAssignments =
            [
                state.VoiceAssignments[0] with
                {
                    ReferenceClipArtifactId = staleReferenceArtifactId,
                    RequiresConsent = true
                }
            ]
        };

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.NotEqual(staleReferenceArtifactId, take.ReferenceClipArtifactId);
        ProjectArtifact refreshedReference = Assert.Single(
            context.MediaAssetRepository.Artifacts,
            artifact => artifact.Kind == ArtifactKind.ReferenceClip);
        Assert.Equal(take.ReferenceClipArtifactId, refreshedReference.Id);
        Assert.Contains("auto-speaker-reference:v2", refreshedReference.Provenance);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_defaults_to_stock_for_synthetic_voice_with_reference_clip()
    {
        var consent = new FakeConsentService();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var auditLog = new FakeAuditLog();
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            auditLog,
            new FakeReferenceClipAnalyzer(),
            workspace.Root);
        (TranscriptProjectState state, Guid speakerId, Guid referenceClipArtifactId) =
            CreateVoiceClonedTranslatedProjectState(context.ArtifactStore);
        state = state with
        {
            VoiceAssignments =
            [
                state.VoiceAssignments[0].AssignVoice("kokoro-onnx", "af_heart", referenceClipArtifactId: referenceClipArtifactId)
            ]
        };

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeKind.Stock, take.Kind);
        Assert.Null(take.ReferenceClipArtifactId);
        Assert.Null(ttsEngine.LastReferenceClipArtifactId);
        Assert.Empty(auditLog.Entries);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_analyzes_reference_clip_only_once_for_multiple_segments()
    {
        var consent = new FakeConsentService();
        consent.GrantVoiceCloningConsent();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var auditLog = new FakeAuditLog();
        var analyzer = new FakeReferenceClipAnalyzer();
        using var workspace = new TemporaryTestWorkspace();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            auditLog,
            analyzer,
            workspace.Root);
        (TranscriptProjectState state, Guid speakerId, _) = CreateVoiceClonedTranslatedProjectStateWithMultipleSegments(context.ArtifactStore);

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, UseReferenceClipForVoiceCloning: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, context.TtsTakeRepository.All.Count);
        Assert.Equal(1, analyzer.AnalyzeCallCount);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_without_reference_clip_saves_stock_take()
    {
        var consent = new FakeConsentService();
        var ttsEngine = new FakeVoiceCloneTtsEngine(consent) { SampleRate = 1000, DurationSamples = 1000 };
        var auditLog = new FakeAuditLog();
        TtsServiceContext context = CreateTtsServiceContext(
            ttsEngine,
            consent,
            auditLog,
            new FakeReferenceClipAnalyzer());
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(context.TtsTakeRepository.All);
        Assert.Equal(TtsTakeKind.Stock, take.Kind);
        Assert.Null(take.ReferenceClipArtifactId);
        Assert.Null(ttsEngine.LastReferenceClipArtifactId);
        Assert.Empty(auditLog.Entries);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_preserves_non_cloning_preferred_alias_containing_f5()
    {
        var ttsEngine = new FakeTtsEngine();
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, PreferredModelAlias: "bf5-asr"),
            TestContext.Current.CancellationToken);

        Assert.Equal("bf5-asr", ttsEngine.LastOptions?.PreferredModelAlias);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_redirects_explicit_voice_clone_alias_without_reference_clip()
    {
        var ttsEngine = new FakeTtsEngine();
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid speakerId = state.Speakers[0].Id;

        await context.Service.GenerateTtsForSpeakerAsync(
            state,
            new GenerateTtsForSpeakerRequest(speakerId, PreferredModelAlias: "f5-tts"),
            TestContext.Current.CancellationToken);

        Assert.Equal("kokoro-onnx", ttsEngine.LastOptions?.PreferredModelAlias);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_throws_when_assigned_stock_voice_is_missing()
    {
        var ttsEngine = new FakeTtsEngine();
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid speakerId = state.Speakers[0].Id;
        state = state with
        {
            VoiceAssignments =
            [
                VoiceAssignment.Create(
                    state.ProjectState.Project.Id,
                    speakerId,
                    "kokoro-onnx",
                    "missing_voice")
            ]
        };

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.GenerateTtsForSpeakerAsync(
                state,
                new GenerateTtsForSpeakerRequest(speakerId),
                TestContext.Current.CancellationToken));

        Assert.Equal("Voicepack 'missing_voice' is not available.", exception.Message);
        Assert.Equal(0, ttsEngine.SynthesizeCallCount);
        Assert.Empty(context.TtsTakeRepository.All);
        Assert.DoesNotContain(context.MediaAssetRepository.Artifacts, artifact => artifact.Kind == ArtifactKind.TtsTake);
    }

    [Fact]
    public async Task TtsOrchestrationService_GenerateTtsForSpeakerAsync_throws_when_translation_revision_has_no_segments()
    {
        var ttsEngine = new FakeTtsEngine();
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);
        TranscriptProjectState state = CreateTranslatedProjectState() with { TranslatedSegments = [] };
        Guid speakerId = state.Speakers[0].Id;

        InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            context.Service.GenerateTtsForSpeakerAsync(
                state,
                new GenerateTtsForSpeakerRequest(speakerId),
                TestContext.Current.CancellationToken));

        Assert.Equal("The current translation revision has no translated segments.", exception.Message);
        Assert.Equal(0, ttsEngine.SynthesizeCallCount);
    }

    [Fact]
    public async Task TtsOrchestrationService_PreviewVoiceAsync_uses_fake_engine_without_persistence()
    {
        var ttsEngine = new FakeTtsEngine();
        TtsServiceContext context = CreateTtsServiceContext(ttsEngine);

        PreviewVoiceResult result = await context.Service.PreviewVoiceAsync(
            new PreviewVoiceRequest("af_heart", "en-us", "Preview text."),
            TestContext.Current.CancellationToken);

        Assert.NotEmpty(result.WavBytes);
        Assert.Equal("fake", result.Provider);
        Assert.Equal("fake", result.ModelId);
        Assert.Equal("af_heart", result.VoiceId);
        Assert.Equal("Preview text.", ttsEngine.LastInputText);
        Assert.Equal("af_heart", ttsEngine.LastVoicepack?.VoiceId);
        Assert.Equal("kokoro-onnx", ttsEngine.LastOptions?.PreferredModelAlias);
        Assert.True(ttsEngine.LastOptions?.RequirePreferredModelAlias);
        Assert.Empty(context.TtsTakeRepository.All);
        Assert.Empty(context.MediaAssetRepository.Artifacts);
        Assert.Empty(context.StageRunStore.All);
    }

    private static TranslationServiceContext CreateTranslationServiceContext(
        FakeTranslationEngine translationEngine,
        FakeGlossaryRepository? glossaryRepository = null,
        FakeTranslationLanguageRouter? translationLanguageRouter = null,
        ITranslatedWordAlignmentService? translatedWordAlignmentService = null)
    {
        var translationRepository = new FakeTranslationRepository();
        glossaryRepository ??= new FakeGlossaryRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var artifactStore = new FakeArtifactStore();
        var fileFingerprintService = new FakeFileFingerprintService();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var artifactWriter = new TranscriptArtifactWriter(
            artifactStore,
            fileFingerprintService,
            mediaAssetRepository);
        var service = new TranslationOrchestrationService(
            translationRepository,
            new GlossaryService(glossaryRepository),
            new GlossaryTermMatcher(),
            translationLanguageRouter ?? new FakeTranslationLanguageRouter(),
            translationEngine,
            ttsTakeRepository,
            stageRunStore,
            artifactStore,
            artifactWriter,
            translatedWordAlignmentService: translatedWordAlignmentService);

        return new TranslationServiceContext(
            service,
            translationRepository,
            stageRunStore,
            artifactStore,
            mediaAssetRepository);
    }

    private static TtsServiceContext CreateTtsServiceContext(
        FakeTtsEngine ttsEngine,
        IConsentService? consentService = null,
        IVoiceCloneAuditLog? auditLog = null,
        IReferenceClipAnalyzer? referenceClipAnalyzer = null,
        string? artifactRoot = null,
        IAudioClipExtractor? audioClipExtractor = null,
        FakeReferenceClipTrimmer? referenceClipTrimmer = null)
    {
        var voiceAssignmentRepository = new FakeVoiceAssignmentRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var voiceCatalog = new FakeVoiceCatalog();
        var artifactStore = new FakeArtifactStore(artifactRoot);
        var fileFingerprintService = new FakeFileFingerprintService();
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var stageRunStore = new FakeProjectStageRunStore();
        var durationAnalysisService = new DurationAnalysisService();
        audioClipExtractor ??= new FakeAudioClipExtractor();
        referenceClipTrimmer ??= new FakeReferenceClipTrimmer();
        var startTtsStageHandler = new StartTtsStageHandler(
            ttsEngine,
            voiceCatalog,
            artifactStore,
            fileFingerprintService,
            mediaAssetRepository,
            ttsTakeRepository,
            stageRunStore,
            durationAnalysisService,
            consentService: consentService,
            voiceCloneAuditLog: auditLog,
            referenceClipAnalyzer: referenceClipAnalyzer);
        var service = new TtsOrchestrationService(
            startTtsStageHandler,
            voiceAssignmentRepository,
            ttsTakeRepository,
            ttsEngine,
            voiceCatalog,
            artifactStore,
            fileFingerprintService,
            mediaAssetRepository,
            referenceClipTrimmer,
            durationAnalysisService,
            audioClipExtractor: audioClipExtractor,
            referenceClipAnalyzer: referenceClipAnalyzer);

        return new TtsServiceContext(
            service,
            ttsTakeRepository,
            stageRunStore,
            artifactStore,
            mediaAssetRepository,
            voiceAssignmentRepository,
            referenceClipTrimmer);
    }

    private static TranscriptProjectState CreateTranscriptProjectState(
        string transcriptLanguage = "en",
        TranslationRevision? translationRevision = null,
        IReadOnlyList<TranslatedSegment>? translatedSegments = null,
        IReadOnlyList<VoiceAssignment>? voiceAssignments = null)
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        Guid projectId = Guid.NewGuid();
        var project = new TrackdubProject(projectId, "Test Project", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            4.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var projectState = new OpenProjectResult(
            project,
            mediaAsset,
            null,
            SourceMediaStatus.Available,
            null,
            [],
            transcriptLanguage);
        TranscriptRevision transcriptRevision = TranscriptRevision.Create(
            projectId,
            stageRunId: null,
            revisionNumber: 1,
            now);
        var speaker = new ProjectSpeaker(Guid.NewGuid(), projectId, "Speaker 1", now);
        TranscriptSegment[] transcriptSegments =
        [
            TranscriptSegment.Create(
                transcriptRevision.Id,
                0,
                0.0d,
                2.0d,
                "Hello there.",
                speaker.Id,
                transcriptLanguage),
            TranscriptSegment.Create(
                transcriptRevision.Id,
                1,
                2.0d,
                4.0d,
                "Second line.",
                speaker.Id,
                transcriptLanguage)
        ];

        return new TranscriptProjectState(
            projectState,
            transcriptRevision,
            transcriptSegments,
            [speaker],
            [],
            translationRevision,
            translatedSegments ?? [],
            false,
            transcriptLanguage,
            [],
            [],
            translationRevision?.TargetLanguage,
            new HashSet<int>(),
            null,
            new FakeVoiceCatalog().GetVoices(),
            voiceAssignments ?? [],
            [],
            [],
            []);
    }

    private static TranscriptProjectState CreateTranslatedProjectState()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TranscriptProjectState state = CreateTranscriptProjectState();
        Guid projectId = state.ProjectState.Project.Id;
        Guid speakerId = state.Speakers[0].Id;
        TranslationRevision translationRevision = TranslationRevision.Create(
            projectId,
            stageRunId: null,
            state.CurrentTranscriptRevision!.Id,
            "es",
            revisionNumber: 1,
            now,
            translationProvider: "fake",
            modelId: "fake-translation-model");
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            translationRevision.Id,
            0,
            0.0d,
            2.0d,
            "Linea traducida.");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(
            projectId,
            speakerId,
            "kokoro-onnx",
            "af_heart");

        return state with
        {
            CurrentTranslationRevision = translationRevision,
            TranslatedSegments = [translatedSegment],
            SelectedTranslationTargetLanguage = "es",
            VoiceAssignments = [voiceAssignment]
        };
    }

    private static TranscriptProjectState CreateTranslatedProjectStateWithWords()
    {
        DateTimeOffset now = DateTimeOffset.UtcNow;
        TranscriptProjectState state = CreateTranscriptProjectState();
        Guid projectId = state.ProjectState.Project.Id;
        Guid speakerId = state.Speakers[0].Id;
        TranslationRevision translationRevision = TranslationRevision.Create(
            projectId,
            stageRunId: null,
            state.CurrentTranscriptRevision!.Id,
            "es",
            revisionNumber: 1,
            now,
            translationProvider: "fake",
            modelId: "fake-translation-model");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(
            projectId,
            speakerId,
            "kokoro-onnx",
            "af_heart");

        return state with
        {
            CurrentTranslationRevision = translationRevision,
            TranslatedSegments =
            [
                TranslatedSegment.Create(
                    translationRevision.Id,
                    0,
                    0.0d,
                    2.0d,
                    "Linea traducida.",
                    "hash-0",
                    [
                        TranslatedWord.Create(0, 0.0d, 1.0d, "Linea"),
                        TranslatedWord.Create(1, 1.0d, 2.0d, "traducida")
                    ]),
                TranslatedSegment.Create(
                    translationRevision.Id,
                    1,
                    2.0d,
                    4.0d,
                    "Segunda linea.",
                    "hash-1",
                    [
                        TranslatedWord.Create(0, 2.0d, 3.0d, "Segunda"),
                        TranslatedWord.Create(1, 3.0d, 4.0d, "linea")
                    ])
            ],
            SelectedTranslationTargetLanguage = "es",
            VoiceAssignments = [voiceAssignment]
        };
    }

    private static (TranscriptProjectState State, Guid SpeakerId, Guid ReferenceClipArtifactId) CreateVoiceClonedTranslatedProjectState(
        FakeArtifactStore artifactStore)
    {
        TranscriptProjectState state = CreateTranslatedProjectState();
        Guid projectId = state.ProjectState.Project.Id;
        Guid mediaAssetId = state.ProjectState.MediaAsset!.Id;
        Guid speakerId = state.Speakers[0].Id;
        Guid referenceClipArtifactId = Guid.NewGuid();
        string relativePath = $"artifacts/reference-clips/{speakerId:D}/reference.wav";
        artifactStore.Seed(relativePath, [0, 1, 2, 3]);

        ProjectArtifact referenceArtifact = new(
            referenceClipArtifactId,
            projectId,
            mediaAssetId,
            ArtifactKind.ReferenceClip,
            relativePath,
            "reference-sha",
            4,
            5d,
            24000,
            1,
            DateTimeOffset.UtcNow,
            Provenance: $"speaker-reference:{speakerId:D}");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(
            projectId,
            speakerId,
            VoiceCloningDefaults.ChatterboxPrimaryAlias,
            requiresConsent: true,
            referenceClipArtifactId: referenceClipArtifactId);

        return (state with
        {
            ProjectState = state.ProjectState with
            {
                Artifacts = state.ProjectState.Artifacts.Concat([referenceArtifact]).ToArray()
            },
            VoiceAssignments = [voiceAssignment]
        }, speakerId, referenceClipArtifactId);
    }

    private static (TranscriptProjectState State, Guid SpeakerId, Guid ReferenceClipArtifactId) CreateVoiceClonedTranslatedProjectStateWithMultipleSegments(
        FakeArtifactStore artifactStore)
    {
        (TranscriptProjectState state, Guid speakerId, Guid referenceClipArtifactId) = CreateVoiceClonedTranslatedProjectState(artifactStore);

        // Add a second translated segment so that HandleAsync iterates over two segments.
        TranslatedSegment secondSegment = TranslatedSegment.Create(
            state.CurrentTranslationRevision!.Id,
            1,
            2.0d,
            4.0d,
            "Segunda linea.");

        return (state with
        {
            TranslatedSegments = [.. state.TranslatedSegments, secondSegment]
        }, speakerId, referenceClipArtifactId);
    }

    private static TranscriptProjectState AddNormalizedAudioArtifact(
        TranscriptProjectState state,
        FakeArtifactStore artifactStore)
    {
        Guid artifactId = Guid.NewGuid();
        string relativePath = "artifacts/audio/normalized.wav";
        artifactStore.Seed(relativePath, [0, 1, 2, 3]);
        ProjectArtifact artifact = new(
            artifactId,
            state.ProjectState.Project.Id,
            state.ProjectState.MediaAsset!.Id,
            ArtifactKind.NormalizedAudio,
            relativePath,
            "normalized-sha",
            4,
            4d,
            24000,
            1,
            DateTimeOffset.UtcNow);

        return state with
        {
            ProjectState = state.ProjectState with
            {
                Artifacts = state.ProjectState.Artifacts.Concat([artifact]).ToArray()
            }
        };
    }

    private sealed record TranslationServiceContext(
        TranslationOrchestrationService Service,
        FakeTranslationRepository TranslationRepository,
        FakeProjectStageRunStore StageRunStore,
        FakeArtifactStore ArtifactStore,
        FakeMediaAssetRepository MediaAssetRepository);

    private sealed record TtsServiceContext(
        TtsOrchestrationService Service,
        FakeTtsTakeRepository TtsTakeRepository,
        FakeProjectStageRunStore StageRunStore,
        FakeArtifactStore ArtifactStore,
        FakeMediaAssetRepository MediaAssetRepository,
        FakeVoiceAssignmentRepository VoiceAssignmentRepository,
        FakeReferenceClipTrimmer ReferenceClipTrimmer);

    private sealed class FakeTranslatedWordAlignmentService(
        Func<TranslatedWordAlignmentRequest, TranslatedWordAlignmentResult> handler) : ITranslatedWordAlignmentService
    {
        public Task<TranslatedWordAlignmentResult> AlignAsync(
            TranslatedWordAlignmentRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(handler(request));
    }

    private sealed class TemporaryTestWorkspace : IDisposable
    {
        public TemporaryTestWorkspace()
        {
            Root = Path.Combine(Path.GetTempPath(), "Trackdub.Tests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Root);
        }

        public string Root { get; }

        public void Dispose()
        {
            if (Directory.Exists(Root))
            {
                Directory.Delete(Root, recursive: true);
            }
        }
    }

    private sealed class FakeAudioClipExtractor : IAudioClipExtractor
    {
        public IReadOnlyList<AudioClipRange> LastRanges { get; private set; } = [];

        public async Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            double startSeconds,
            double endSeconds,
            string destinationPath,
            CancellationToken cancellationToken) =>
            await ExtractAsync(
                sourceWavePath,
                [new AudioClipRange(startSeconds, endSeconds)],
                destinationPath,
                cancellationToken).ConfigureAwait(false);

        public async Task<AudioClipExtractionResult> ExtractAsync(
            string sourceWavePath,
            IReadOnlyList<AudioClipRange> ranges,
            string destinationPath,
            CancellationToken cancellationToken)
        {
            LastRanges = ranges.ToArray();
            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            await File.WriteAllBytesAsync(destinationPath, [1, 2, 3, 4], cancellationToken);
            return new AudioClipExtractionResult(
                destinationPath,
                ranges.Sum(range => range.EndSeconds - range.StartSeconds),
                24000,
                1);
        }
    }
}
