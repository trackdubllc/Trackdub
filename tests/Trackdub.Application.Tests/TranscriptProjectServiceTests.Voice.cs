using Trackdub.Contracts;
using Trackdub.Contracts.Licensing;
using Trackdub.Application.Mixing;
using Trackdub.Application.Projects;
using Trackdub.Application.Transcripts;
using Trackdub.Application.Transcripts.Stages;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Domain.Artifacts;
using Trackdub.Domain.AudioQuality;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Speakers;
using Trackdub.Domain.StageRuns;
using Trackdub.Domain.Transcript;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Tts;
using Trackdub.TestDoubles;
using System.Security.Cryptography;

#pragma warning disable CS0618

namespace Trackdub.Application.Tests;

public partial class TranscriptProjectServiceTests
{
    [Fact]
    public async Task AssignVoiceToSpeakerAsync_persists_assignment_and_reports_language_mismatch()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(new CreateTranscriptProjectRequest("Transcript Demo", sourcePath), TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState assigned = await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        VoiceAssignment assignment = Assert.Single(scope.VoiceAssignmentRepository.Assignments);
        Assert.Equal(translated.Speakers[0].Id, assignment.SpeakerId);
        Assert.Equal("kokoro-onnx", assignment.VoiceModelId);
        Assert.Equal("af_heart", assignment.VoiceVariant);
        VoiceAssignmentWarning warning = Assert.Single(assigned.VoiceAssignmentWarnings);
        Assert.Equal(translated.Speakers[0].Id, warning.SpeakerId);
    }

    [Fact]
    public async Task GenerateTtsForSpeakerAsync_writes_take_artifact_metadata_and_duration_warning()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var ttsEngine = new FakeTtsEngine { DurationSamples = 168000 };
        FakeServiceScope scope = CreateScope(tempDirectory, ttsEngine: ttsEngine);
        await scope.Service.CreateAsync(new CreateTranscriptProjectRequest("Transcript Demo", sourcePath), TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState tts = await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(TtsTakeStatus.Completed, take.Status);
        Assert.Equal("fake", take.ModelId);
        Assert.Equal("af_heart", take.VoiceId);
        Assert.Equal(24000, take.SampleRate);
        Assert.True(take.DurationOverrunRatio > 0.10d);
        ProjectArtifact artifact = Assert.Single(tts.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.TtsTake);
        Assert.Equal(ArtifactKind.TtsTake, artifact.Kind);
        Assert.True(scope.ArtifactStore.Exists(artifact.RelativePath));
        TtsSegmentState segmentState = Assert.Single(tts.TtsSegmentStates, state => state.SegmentIndex == 0);
        Assert.True(segmentState.HasDurationWarning);
    }

    [Fact]
    public async Task GenerateTtsForSegmentAsync_writes_take_for_selected_segment_only()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        await scope.Service.GenerateTtsForSegmentAsync(
            new GenerateTtsForSegmentRequest(
                translated.CurrentTranscriptRevision!.Id,
                translated.TranscriptSegments[1].Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(1, take.SegmentIndex);
    }

    [Fact]
    public async Task GenerateTtsForSpeakerAsync_auto_stretches_mild_overrun()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 2300 };
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            ttsEngine: ttsEngine,
            transcriptionEngine: CreateSingleSegmentTranscription());
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState tts = await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(TtsStretchMode.Automatic, take.StretchMode);
        Assert.Equal(TtsStretchEngine.Atempo, take.StretchEngine);
        Assert.Equal(2.3d, take.PreStretchDurationSeconds!.Value, precision: 6);
        Assert.Equal(1.15d, take.StretchRatioApplied!.Value, precision: 6);
        Assert.Equal(2000, take.DurationSamples);
        Assert.Equal(0.15d, take.DurationOverrunRatio!.Value, precision: 6);
        Assert.Equal(1.15d, Assert.Single(scope.AudioTimeStretchService.StretchRatios), precision: 6);
        ProjectArtifact artifact = Assert.Single(tts.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.TtsTake);
        Assert.Equal(2.0d, artifact.DurationSeconds);
    }

    [Fact]
    public async Task GenerateTtsForSpeakerAsync_uses_postprocessed_duration_for_timing_analysis()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 2300 };
        var postProcessor = new FakeTtsAudioPostProcessor(durationSamples: 2000, leadingTrimmedSamples: 200, trailingTrimmedSamples: 100);
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            ttsEngine: ttsEngine,
            transcriptionEngine: CreateSingleSegmentTranscription(),
            ttsAudioPostProcessor: postProcessor);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState tts = await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(TtsStretchMode.None, take.StretchMode);
        Assert.Equal(0d, take.DurationOverrunRatio!.Value, precision: 6);
        Assert.Equal(2000, take.DurationSamples);
        Assert.Equal(2300, postProcessor.LastRequest!.DurationSamples);
        Assert.Empty(scope.AudioTimeStretchService.StretchRatios);
        ProjectArtifact artifact = Assert.Single(tts.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.TtsTake);
        Assert.Equal(2.0d, artifact.DurationSeconds);
    }

    [Fact]
    public async Task GenerateTtsForSpeakerAsync_flags_large_overrun_without_auto_stretch()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 2500 };
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            ttsEngine: ttsEngine,
            transcriptionEngine: CreateSingleSegmentTranscription());
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState tts = await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(TtsStretchMode.None, take.StretchMode);
        Assert.Empty(scope.AudioTimeStretchService.StretchRatios);
        TtsSegmentState segmentState = Assert.Single(tts.TtsSegmentStates);
        Assert.True(segmentState.HasDurationWarning);
        Assert.True(segmentState.CanManualStretch);
    }

    [Fact]
    public async Task StretchTtsTakeAsync_manually_stretches_overrun_take()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 2500 };
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            ttsEngine: ttsEngine,
            transcriptionEngine: CreateSingleSegmentTranscription());
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);
        await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        TranscriptProjectState stretched = await scope.Service.StretchTtsTakeAsync(
            new StretchTtsTakeRequest(scope.TtsTakeRepository.Takes.Single().Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(TtsStretchMode.Manual, take.StretchMode);
        Assert.Equal(1.25d, take.StretchRatioApplied!.Value, precision: 6);
        Assert.Equal(2.5d, take.PreStretchDurationSeconds!.Value, precision: 6);
        Assert.Equal(2000, take.DurationSamples);
        Assert.Equal(1.25d, Assert.Single(scope.AudioTimeStretchService.StretchRatios), precision: 6);
        Assert.Equal(TtsStretchMode.Manual, Assert.Single(stretched.TtsSegmentStates).StretchMode);
    }

    [Fact]
    public async Task StretchTtsTakeAsync_manually_stretches_underrun_take()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var ttsEngine = new FakeTtsEngine { SampleRate = 1000, DurationSamples = 1500 };
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            ttsEngine: ttsEngine,
            transcriptionEngine: CreateSingleSegmentTranscription());
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);
        await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        await scope.Service.StretchTtsTakeAsync(
            new StretchTtsTakeRequest(scope.TtsTakeRepository.Takes.Single().Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(TtsStretchMode.Manual, take.StretchMode);
        Assert.Equal(0.75d, take.StretchRatioApplied!.Value, precision: 6);
        Assert.Equal(0d, take.DurationOverrunRatio!.Value, precision: 6);
        Assert.Equal(0.75d, Assert.Single(scope.AudioTimeStretchService.StretchRatios), precision: 6);
    }

    [Fact]
    public async Task GenerateTtsForAllSpeakersAsync_uses_fallback_voice_without_user_assignment()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState tts = await scope.Service.GenerateTtsForAllSpeakersAsync(
            new GenerateTtsForAllSpeakersRequest(new Dictionary<Guid, string>
            {
                [created.Speakers[0].Id] = "af_heart"
            }),
            TestContext.Current.CancellationToken);

        Assert.Equal(translated.TranscriptSegments.Count, scope.TtsTakeRepository.Takes.Count);
        Assert.Empty(tts.VoiceAssignments);
        Assert.All(scope.VoiceAssignmentRepository.Assignments, assignment => Assert.True(assignment.IsFallback));
    }

    [Fact]
    public async Task PreviewVoiceAsync_synthesizes_sample_without_persisting_tts_take()
    {
        string tempDirectory = CreateTempDirectory();
        FakeServiceScope scope = CreateScope(tempDirectory);

        PreviewVoiceResult result = await scope.Service.PreviewVoiceAsync(
            new PreviewVoiceRequest("af_heart", "en-us", "Hello, my name's Heart."),
            TestContext.Current.CancellationToken);

        Assert.Equal("af_heart", result.VoiceId);
        Assert.NotEmpty(result.WavBytes);
        Assert.Equal("Hello, my name's Heart.", scope.TtsEngine.LastInputText);
        Assert.Equal("af_heart", scope.TtsEngine.LastVoicepack?.VoiceId);
        Assert.Empty(scope.TtsTakeRepository.Takes);
        Assert.Empty(scope.VoiceAssignmentRepository.Assignments);
    }

    [Fact]
    public async Task Voice_assignment_change_marks_existing_takes_stale_and_batch_regeneration_creates_fresh_take()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(new CreateTranscriptProjectRequest("Transcript Demo", sourcePath), TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        Guid speakerId = translated.Speakers[0].Id;
        await scope.Service.AssignVoiceToSpeakerAsync(new AssignVoiceToSpeakerRequest(speakerId, "af_heart"), TestContext.Current.CancellationToken);
        await scope.Service.GenerateTtsForSpeakerAsync(new GenerateTtsForSpeakerRequest(speakerId), TestContext.Current.CancellationToken);

        TranscriptProjectState reassigned = await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(speakerId, "am_adam"),
            TestContext.Current.CancellationToken);

        TtsTake staleTake = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.True(staleTake.IsStale);
        Assert.Contains(reassigned.TtsSegmentStates, state => state.SegmentIndex == 0 && state.IsStale);

        TranscriptProjectState regenerated = await scope.Service.RegenerateStaleTtsForSpeakerAsync(
            new RegenerateStaleTtsForSpeakerRequest(speakerId),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, scope.TtsTakeRepository.Takes.Count);
        Assert.Equal(2, scope.TtsEngine.SynthesizeCallCount);
        TtsSegmentState currentSegment = Assert.Single(regenerated.TtsSegmentStates, state => state.SegmentIndex == 0);
        Assert.False(currentSegment.IsStale);
        Assert.Equal("am_adam", scope.TtsTakeRepository.Takes.OrderBy(take => take.CreatedAtUtc).Last().VoiceId);
    }

    [Fact]
    public async Task AssignVoiceToSpeakerAsync_preserves_reference_clip_when_stock_voice_unchanged()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(new CreateTranscriptProjectRequest("Transcript Demo", sourcePath), TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        Guid speakerId = translated.Speakers[0].Id;
        await scope.Service.AssignVoiceToSpeakerAsync(new AssignVoiceToSpeakerRequest(speakerId, "af_heart"), TestContext.Current.CancellationToken);

        Guid referenceClipArtifactId = Guid.NewGuid();
        VoiceAssignment clonedAssignment = scope.VoiceAssignmentRepository.Assignments.Single().AssignReferenceClip(referenceClipArtifactId);
        scope.VoiceAssignmentRepository.Assignments[0] = clonedAssignment;
        TranslatedSegment translatedSegment = translated.TranslatedSegments[0];
        scope.TtsTakeRepository.Takes.Add(
            TtsTake.CreateVoiceCloned(
                    translated.ProjectState.Project.Id,
                    clonedAssignment.Id,
                    referenceClipArtifactId,
                    translatedSegment.Id,
                    translatedSegment.SegmentIndex)
                .Complete(Guid.NewGuid(), durationSamples: 1000, sampleRate: 1000, provider: "fake"));

        await scope.Service.AssignVoiceToSpeakerAsync(new AssignVoiceToSpeakerRequest(speakerId, "af_heart"), TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.False(take.IsStale);
        Assert.Equal(referenceClipArtifactId, scope.VoiceAssignmentRepository.Assignments.Single().ReferenceClipArtifactId);
    }

}
