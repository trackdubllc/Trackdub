using Trackdub.Contracts;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Mixing;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class StageContractsTests
{
    [Fact]
    public void Future_stage_contracts_use_canonical_stage_names()
    {
        Assert.Equal("preview-mix", StageNames.PreviewMix);
        Assert.Equal("voice-cloning", StageNames.VoiceCloning);
        Assert.Equal("export", StageNames.Export);
        Assert.Equal("speech-enhancement", StageNames.SpeechEnhancement);
        Assert.Equal("audio-preparation", StageNames.AudioPreparation);

        var previewRequest = new PreviewMixStageRequest(Guid.NewGuid(), 1.0d, 2.0d);
        var voiceCloningRequest = new VoiceCloningStageRequest(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), new HashSet<int> { 1 });
        var exportRequest = new ExportStageRequest(
            Guid.NewGuid(),
            "output.mp4",
            [ExportSubtitleFormat.Srt],
            TargetLufs: -14.0d);

        Assert.Equal(1.0d, previewRequest.StartSeconds);
        Assert.False(previewRequest.RestoreOriginalPan);
        Assert.Single(voiceCloningRequest.SegmentIndices);
        Assert.Equal(ExportSubtitleFormat.Srt, Assert.Single(exportRequest.SubtitleFormats));
        Assert.False(exportRequest.RestoreOriginalPan);
    }

    [Fact]
    public void Future_stage_results_carry_stage_run_and_outputs()
    {
        Guid projectId = Guid.NewGuid();
        StageRunRecord previewRun = StageRunRecord.Start(projectId, StageNames.PreviewMix, DateTimeOffset.UtcNow);
        StageRunRecord voiceRun = StageRunRecord.Start(projectId, StageNames.VoiceCloning, DateTimeOffset.UtcNow);
        StageRunRecord exportRun = StageRunRecord.Start(projectId, StageNames.Export, DateTimeOffset.UtcNow);

        var mixPlan = new MixPlan(
            projectId,
            MediaAssetId: null,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            SourceGainDb: 0d,
            DubbedSpeechGainDb: 0d,
            DuckingGainDb: -12d,
            DuckingLeadSeconds: 0.05d,
            DuckingTailSeconds: 0.18d,
            DateTimeOffset.UtcNow,
            SpeechClips: [],
            DuckingRegions: [],
            Warnings: [new MixPlanWarning(0, Guid.NewGuid(), "missing take")]);
        var previewResult = new PreviewMixStageResult(previewRun, "artifacts/preview.wav", mixPlan, 1.0d, mixPlan.Warnings);
        var voiceResult = new VoiceCloningStageResult(voiceRun, [Guid.NewGuid()]);
        var exportResult = new ExportStageResult(
            exportRun,
            "output.mp4",
            "output.manifest.json",
            "artifacts/export/export.wav",
            "artifacts/export/export.mp4",
            [],
            []);

        Assert.Equal(StageNames.PreviewMix, previewResult.StageRun.StageName);
        Assert.Single(previewResult.Warnings);
        Assert.Equal(StageNames.VoiceCloning, voiceResult.StageRun.StageName);
        Assert.Equal(StageNames.Export, exportResult.StageRun.StageName);
        Assert.Equal("output.manifest.json", exportResult.ManifestPath);
    }

    [Fact]
    public async Task VadStageHandler_uses_configured_fake_speech_regions()
    {
        var detector = new FakeSpeechRegionDetector();
        detector.SetRegions(new SpeechRegion(4, 1.0d, 2.5d));
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new VadStageHandler(detector, stageRunStore);

        VadStageResult result = await handler.HandleAsync(
            new VadStageRequest(Guid.NewGuid(), "virtual-normalized.wav", 12.0d),
            TestContext.Current.CancellationToken);

        SpeechRegion region = Assert.Single(result.Regions);
        Assert.Equal(4, region.Index);
        Assert.Equal("virtual-normalized.wav", detector.LastNormalizedAudioPath);
        Assert.Equal(12.0d, detector.LastDurationSeconds);
        Assert.Equal(1, detector.DetectCallCount);
        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal("fake-vad", result.StageRun.RuntimeInfo?.ModelAlias);
    }

    [Fact]
    public async Task StartTtsStageHandler_uses_fake_file_fingerprint_service()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "virtual-source.mp4",
            "virtual-source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            12.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        TranscriptSegment transcriptSegment = TranscriptSegment.Create(
            Guid.NewGuid(),
            0,
            1.0d,
            2.0d,
            "Hello.",
            speakerId,
            "en");
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            Guid.NewGuid(),
            0,
            1.0d,
            2.0d,
            "Hola.");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(projectId, speakerId, "af_heart");
        var artifactStore = new FakeArtifactStore();
        var fingerprintService = new FakeFileFingerprintService(
            new FileFingerprint("tts-hash", 42, now));
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        using var handler = new StartTtsStageHandler(
            new FakeTtsEngine(),
            new FakeVoiceCatalog(),
            artifactStore,
            fingerprintService,
            mediaAssetRepository,
            ttsTakeRepository,
            new FakeProjectStageRunStore());

        StartTtsStageResult result = await handler.HandleAsync(
            new StartTtsStageRequest(
                projectId,
                mediaAsset,
                speakerId,
                "es",
                voiceAssignment,
                [transcriptSegment],
                [translatedSegment]),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(result.Takes);
        ProjectArtifact artifact = Assert.Single(mediaAssetRepository.Artifacts);
        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal(ArtifactKind.TtsTake, artifact.Kind);
        Assert.Equal("tts-hash", artifact.Sha256);
        Assert.Equal(42, artifact.SizeBytes);
        Assert.Equal(artifact.Id, take.ArtifactId);
        Assert.Equal(artifact.RelativePath, Assert.Single(fingerprintService.RequestedPaths));
        Assert.True(artifactStore.Exists(artifact.RelativePath));
    }

    [Fact]
    public async Task StemSeparationStageHandler_uses_fake_file_fingerprint_service()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "virtual-source.mp4",
            "virtual-source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            12.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var sourceAudioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.NormalizedAudio,
            ProjectArtifactPaths.NormalizedAudioRelativePath,
            "audio-hash",
            100,
            12.0d,
            48000,
            1,
            now);
        var fingerprintService = new FakeFileFingerprintService();
        var handler = new StemSeparationStageHandler(
            new FakeStemSeparationEngine(),
            new FakeArtifactStore(),
            fingerprintService,
            new FakeMediaAssetRepository(),
            new FakeProjectStageRunStore());

        StemSeparationStageResult result = await handler.HandleAsync(
            new StemSeparationStageRequest(projectId, mediaAsset, sourceAudioArtifact, []),
            progress: null,
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal("hash-vocals.wav", result.VocalsArtifact.Sha256);
        Assert.Equal("hash-ambiance.wav", result.AmbianceArtifact.Sha256);
        Assert.NotNull(result.MusicArtifact);
        Assert.NotNull(result.SoundEffectsArtifact);
        Assert.Equal("hash-music.wav", result.MusicArtifact!.Sha256);
        Assert.Equal("hash-sfx.wav", result.SoundEffectsArtifact!.Sha256);
        Assert.Equal(4, fingerprintService.RequestedPaths.Count);
    }

    [Fact]
    public async Task SpeechAudioEnhancementStageHandler_uses_fake_file_fingerprint_service()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "virtual-source.mp4",
            "virtual-source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            12.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var sourceAudioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.Vocals,
            ProjectArtifactPaths.GetStemVocalsRelativePath(Guid.NewGuid()),
            "vocals-hash",
            100,
            12.0d,
            48000,
            1,
            now);
        var fingerprintService = new FakeFileFingerprintService();
        var enhancementService = new FakeSpeechAudioEnhancementService();
        var handler = new SpeechAudioEnhancementStageHandler(
            enhancementService,
            new FakeArtifactStore(),
            fingerprintService,
            new FakeMediaAssetRepository(),
            new FakeProjectStageRunStore());

        SpeechAudioEnhancementStageResult result = await handler.HandleAsync(
            new SpeechAudioEnhancementStageRequest(projectId, mediaAsset, sourceAudioArtifact, []),
            TestContext.Current.CancellationToken);

        Assert.Equal(StageRunStatus.Completed, result.StageRun.Status);
        Assert.Equal(StageNames.SpeechEnhancement, result.StageRun.StageName);
        Assert.Equal(ArtifactKind.SpeechEnhancedAudio, result.EnhancedAudioArtifact.Kind);
        Assert.Equal("hash-speech.wav", result.EnhancedAudioArtifact.Sha256);
        Assert.Single(fingerprintService.RequestedPaths);
        Assert.Equal(1, enhancementService.CallCount);
    }

    [Fact]
    public async Task SpeechAudioEnhancementStageHandler_records_cancellation_as_canceled()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "virtual-source.mp4",
            "virtual-source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            12.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        var sourceAudioArtifact = new ProjectArtifact(
            Guid.NewGuid(),
            projectId,
            mediaAssetId,
            ArtifactKind.Vocals,
            ProjectArtifactPaths.GetStemVocalsRelativePath(Guid.NewGuid()),
            "vocals-hash",
            100,
            12.0d,
            48000,
            1,
            now);
        var stageRunStore = new FakeProjectStageRunStore();
        var handler = new SpeechAudioEnhancementStageHandler(
            new FakeSpeechAudioEnhancementService(),
            new FakeArtifactStore(),
            new FakeFileFingerprintService(),
            new FakeMediaAssetRepository(),
            stageRunStore);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            handler.HandleAsync(
                new SpeechAudioEnhancementStageRequest(projectId, mediaAsset, sourceAudioArtifact, []),
                cancellation.Token));

        StageRunRecord stageRun = Assert.Single(stageRunStore.All);
        Assert.Equal(StageRunStatus.Canceled, stageRun.Status);
        Assert.Equal(1, stageRunStore.UpdateCallCount);
        Assert.Contains("canceled", stageRun.FailureReason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task StartTtsStageHandler_returns_cached_take_without_synthesizing_when_fingerprint_matches()
    {
        // Arrange: run handler once to produce a take with a known fingerprint.
        Guid projectId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(), projectId, "source.mp4", "source.mp4", "h", 100, now,
            "mp4", 10.0, HasAudio: true, HasVideo: true, now);
        TranscriptSegment transcriptSegment = TranscriptSegment.Create(
            Guid.NewGuid(), 0, 0.0, 1.0, "Hello.", speakerId, "en");
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            Guid.NewGuid(), 0, 0.0, 1.0, "Hola.");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(projectId, speakerId, "af_heart");
        var artifactStore = new FakeArtifactStore();
        var fingerprintService = new FakeFileFingerprintService(
            new FileFingerprint("tts-hash", 42, now));
        var mediaAssetRepository = new FakeMediaAssetRepository();
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var fakeTtsEngine = new FakeTtsEngine();
        using var handler = new StartTtsStageHandler(
            fakeTtsEngine,
            new FakeVoiceCatalog(),
            artifactStore,
            fingerprintService,
            mediaAssetRepository,
            ttsTakeRepository,
            new FakeProjectStageRunStore());

        StartTtsStageRequest request = new(
            projectId, mediaAsset, speakerId, "es", voiceAssignment,
            [transcriptSegment], [translatedSegment]);

        // First call: synthesis runs and take is saved.
        StartTtsStageResult first = await handler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, fakeTtsEngine.SynthesizeCallCount);
        TtsTake firstTake = Assert.Single(first.Takes);

        // Second call with identical request: fingerprint cache hit, no synthesis.
        StartTtsStageResult second = await handler.HandleAsync(request, TestContext.Current.CancellationToken);
        Assert.Equal(1, fakeTtsEngine.SynthesizeCallCount); // still 1 — no new synthesis
        TtsTake secondTake = Assert.Single(second.Takes);
        Assert.Equal(firstTake.Id, secondTake.Id);          // same take returned
    }

    [Fact]
    public async Task StartTtsStageHandler_does_not_reuse_cached_take_across_model_variant_aliasesAsync()
    {
        Guid projectId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(), projectId, "source.mp4", "source.mp4", "h", 100, now,
            "mp4", 10.0, HasAudio: true, HasVideo: true, now);
        TranscriptSegment transcriptSegment = TranscriptSegment.Create(
            Guid.NewGuid(), 0, 0.0, 1.0, "Hello.", speakerId, "en");
        TranslatedSegment translatedSegment = TranslatedSegment.Create(
            Guid.NewGuid(), 0, 0.0, 1.0, "Hola.");
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(projectId, speakerId, "af_heart");
        var ttsTakeRepository = new FakeTtsTakeRepository();
        var fakeTtsEngine = new FakeTtsEngine();
        using var handler = new StartTtsStageHandler(
            fakeTtsEngine,
            new FakeVoiceCatalog(),
            new FakeArtifactStore(),
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, now)),
            new FakeMediaAssetRepository(),
            ttsTakeRepository,
            new FakeProjectStageRunStore());

        StartTtsStageRequest request = new(
            projectId, mediaAsset, speakerId, "es", voiceAssignment,
            [transcriptSegment], [translatedSegment],
            PreferredModelVariantAlias: "olive-cpu-fp32");

        StartTtsStageResult first = await handler.HandleAsync(request, TestContext.Current.CancellationToken);
        StartTtsStageResult second = await handler.HandleAsync(
            request with { PreferredModelVariantAlias = "olive-dml-fp16" },
            TestContext.Current.CancellationToken);

        Assert.Equal(2, fakeTtsEngine.SynthesizeCallCount);
        Assert.Equal(2, ttsTakeRepository.All.Count);
        Assert.NotEqual(Assert.Single(first.Takes).Id, Assert.Single(second.Takes).Id);
        Assert.Equal(2, ttsTakeRepository.All.Select(static take => take.InputFingerprint).Distinct().Count());
    }

    [Fact]
    public async Task StartTtsStageHandler_serializes_persistence_during_parallel_synthesis()
    {
        Guid projectId = Guid.NewGuid();
        Guid mediaAssetId = Guid.NewGuid();
        Guid speakerId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var mediaAsset = new MediaAsset(
            mediaAssetId,
            projectId,
            "source.mp4",
            "source.mp4",
            "source-hash",
            100,
            now,
            "mp4",
            10.0d,
            HasAudio: true,
            HasVideo: true,
            now);
        TranscriptSegment[] transcriptSegments =
        [
            TranscriptSegment.Create(Guid.NewGuid(), 0, 0.0d, 1.0d, "One.", speakerId, "en"),
            TranscriptSegment.Create(Guid.NewGuid(), 1, 1.0d, 2.0d, "Two.", speakerId, "en"),
            TranscriptSegment.Create(Guid.NewGuid(), 2, 2.0d, 3.0d, "Three.", speakerId, "en"),
            TranscriptSegment.Create(Guid.NewGuid(), 3, 3.0d, 4.0d, "Four.", speakerId, "en")
        ];
        TranslatedSegment[] translatedSegments = transcriptSegments
            .Select(segment => TranslatedSegment.Create(
                Guid.NewGuid(),
                segment.SegmentIndex,
                segment.StartSeconds,
                segment.EndSeconds,
                $"ES {segment.SegmentIndex}"))
            .ToArray();
        VoiceAssignment voiceAssignment = VoiceAssignment.Create(projectId, speakerId, "af_heart");
        var probe = new PersistenceConcurrencyProbe();
        var mediaAssetRepository = new ProbedMediaAssetRepository(probe);
        var ttsTakeRepository = new ProbedTtsTakeRepository(probe);
        using var handler = new StartTtsStageHandler(
            new DelayingTtsEngine(),
            new FakeVoiceCatalog(),
            new FakeArtifactStore(),
            new FakeFileFingerprintService(new FileFingerprint("tts-hash", 42, now)),
            mediaAssetRepository,
            ttsTakeRepository,
            new FakeProjectStageRunStore());

        StartTtsStageResult result = await handler.HandleAsync(
            new StartTtsStageRequest(
                projectId,
                mediaAsset,
                speakerId,
                "es",
                voiceAssignment,
                transcriptSegments,
                translatedSegments),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, result.Takes.Count);
        Assert.Equal([0, 1, 2, 3], result.Takes.Select(static take => take.SegmentIndex));
        Assert.False(probe.ConcurrentAccessObserved);
    }

    private sealed class DelayingTtsEngine : FakeTtsEngine
    {
        public override async Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(25, cancellationToken).ConfigureAwait(false);
            return await base.SynthesizeAsync(request, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class PersistenceConcurrencyProbe
    {
        private int activeCalls;

        public bool ConcurrentAccessObserved { get; private set; }

        public async Task EnterAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref activeCalls) > 1)
            {
                ConcurrentAccessObserved = true;
            }

            try
            {
                await Task.Delay(15, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }
    }

    private sealed class ProbedTtsTakeRepository(PersistenceConcurrencyProbe probe) : ITtsTakeRepository
    {
        private readonly FakeTtsTakeRepository inner = new();

        public async Task<TtsTake?> GetAsync(Guid id, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetAsync(id, cancellationToken).ConfigureAwait(false);
        }

        public async Task<TtsTake?> GetByFingerprintAsync(Guid projectId, string inputFingerprint, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetByFingerprintAsync(projectId, inputFingerprint, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TtsTake>> GetByProjectAsync(Guid projectId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetByProjectAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TtsTake>> GetBySegmentAsync(Guid translatedSegmentId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetBySegmentAsync(translatedSegmentId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<TtsTake>> GetStaleBySpeakerAsync(Guid projectId, Guid voiceAssignmentId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetStaleBySpeakerAsync(projectId, voiceAssignmentId, cancellationToken).ConfigureAwait(false);
        }

        public async Task MarkBySegmentIndicesStaleAsync(Guid projectId, IReadOnlySet<int> segmentIndices, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.MarkBySegmentIndicesStaleAsync(projectId, segmentIndices, cancellationToken).ConfigureAwait(false);
        }

        public async Task MarkByVoiceAssignmentStaleAsync(Guid projectId, Guid voiceAssignmentId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.MarkByVoiceAssignmentStaleAsync(projectId, voiceAssignmentId, cancellationToken).ConfigureAwait(false);
        }

        public async Task SaveAsync(TtsTake take, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.SaveAsync(take, cancellationToken).ConfigureAwait(false);
        }
    }

    private sealed class ProbedMediaAssetRepository(PersistenceConcurrencyProbe probe) : IMediaAssetRepository
    {
        private readonly FakeMediaAssetRepository inner = new();

        public async Task SaveAsync(MediaAsset asset, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.SaveAsync(asset, cancellationToken).ConfigureAwait(false);
        }

        public async Task UpdateSourcePathAsync(Guid mediaAssetId, string sourceFilePath, string sourceFileName, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.UpdateSourcePathAsync(mediaAssetId, sourceFilePath, sourceFileName, cancellationToken).ConfigureAwait(false);
        }

        public async Task<MediaAsset?> GetPrimaryAsync(Guid projectId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
#pragma warning disable CS0618
            return await inner.GetPrimaryAsync(projectId, cancellationToken).ConfigureAwait(false);
#pragma warning restore CS0618
        }

        public async Task SaveArtifactAsync(ProjectArtifact artifact, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.SaveArtifactAsync(artifact, cancellationToken).ConfigureAwait(false);
        }

        public async Task DeleteArtifactAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            await inner.DeleteArtifactAsync(artifactId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<ProjectArtifact>> GetArtifactsAsync(Guid projectId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetArtifactsAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<IReadOnlyList<MediaAsset>> GetAllAsync(Guid projectId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetAllAsync(projectId, cancellationToken).ConfigureAwait(false);
        }

        public async Task<ProjectArtifact?> GetArtifactByIdAsync(Guid artifactId, CancellationToken cancellationToken)
        {
            await probe.EnterAsync(cancellationToken).ConfigureAwait(false);
            return await inner.GetArtifactByIdAsync(artifactId, cancellationToken).ConfigureAwait(false);
        }
    }
}
