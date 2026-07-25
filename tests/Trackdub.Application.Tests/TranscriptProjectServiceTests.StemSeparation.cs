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
    public async Task CreateAsync_with_stem_separation_routes_vad_diarization_and_asr_to_vocals()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        var diarizationEngine = new RecordingDiarizationEngine();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            diarizationEngine: diarizationEngine,
            transcriptionEngine: transcriptionEngine);

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: true,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        ProjectArtifact vocals = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);
        ProjectArtifact ambiance = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Ambiance);
        ProjectArtifact music = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Music);
        ProjectArtifact sfx = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.SoundEffects);
        ProjectArtifact analysis = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.AudioQualityAnalysis);
        Assert.Equal(12.0d, vocals.DurationSeconds);
        Assert.Equal(12.0d, ambiance.DurationSeconds);
        Assert.Equal(12.0d, music.DurationSeconds);
        Assert.Equal(12.0d, sfx.DurationSeconds);
        Assert.Equal("hash-vocals.wav", vocals.Sha256);
        Assert.Equal("hash-ambiance.wav", ambiance.Sha256);
        Assert.Equal("hash-music.wav", music.Sha256);
        Assert.Equal("hash-sfx.wav", sfx.Sha256);
        Assert.Equal(vocals.RelativePath, result.AsrAudioRelativePath);
        Assert.Equal(ambiance.RelativePath, result.MixSourceAudioRelativePath);
        Assert.Equal(scope.ArtifactStore.GetPath(vocals.RelativePath), transcriptionEngine.LastAudioPath);
        Assert.Equal(scope.ArtifactStore.GetPath(vocals.RelativePath), scope.SpeechRegionDetector.LastNormalizedAudioPath);
        Assert.Equal(scope.ArtifactStore.GetPath(vocals.RelativePath), diarizationEngine.LastAudioPath);
        Assert.Equal(2, scope.AudioQualityAnalyzer.Requests.Count);
        Assert.NotNull(analysis);
        Assert.Equal(1, scope.StemSeparationEngine.CallCount);
        Assert.Contains(result.StageRuns, stageRun => stageRun.StageName == "separation" && stageRun.Status == StageRunStatus.Completed);
        Assert.Contains(result.StageRuns, stageRun => stageRun.StageName == StageNames.AudioPreparation && stageRun.Status == StageRunStatus.Completed);
        Assert.Null(result.StemSeparationWarning);
    }

    [Fact]
    public async Task CreateAsync_when_vocal_stem_is_rejected_keeps_full_mix_route_on_reload()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(new AudioQualityAnalysisResult(
            "full-mix.wav",
            CreateAudioQualityMetrics(SpeechAudioSourceKind.FullMix),
            AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.FullMix),
            [],
            []));
        analyzer.QueueResult(new AudioQualityAnalysisResult(
            "vocals.wav",
            CreateAudioQualityMetrics(SpeechAudioSourceKind.VocalStem) with { ActiveRmsDbfs = -60.0d },
            AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.VocalStem),
            [AudioQualityDefectKind.NearSilence],
            []));
        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: transcriptionEngine,
            audioQualityAnalyzer: analyzer);

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        Assert.Contains(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);
        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, result.AsrAudioRelativePath);
        Assert.Equal(scope.ArtifactStore.GetPath(ProjectArtifactPaths.NormalizedAudioRelativePath), transcriptionEngine.LastAudioPath);
    }

    [Fact]
    public async Task CreateAsync_without_stem_separation_uses_full_mix_audio()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(tempDirectory, transcriptionEngine: transcriptionEngine);

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        ProjectArtifact analysis = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.AudioQualityAnalysis);
        Assert.DoesNotContain(result.ProjectState.Artifacts, artifact => artifact.Kind is ArtifactKind.Vocals or ArtifactKind.Ambiance);
        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, result.AsrAudioRelativePath);
        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, result.MixSourceAudioRelativePath);
        Assert.Equal(scope.ArtifactStore.GetPath(ProjectArtifactPaths.NormalizedAudioRelativePath), transcriptionEngine.LastAudioPath);
        Assert.Equal(scope.ArtifactStore.GetPath(ProjectArtifactPaths.NormalizedAudioRelativePath), scope.SpeechRegionDetector.LastNormalizedAudioPath);
        Assert.NotNull(analysis);
        Assert.Equal(0, scope.StemSeparationEngine.CallCount);
        Assert.Single(scope.AudioQualityAnalyzer.Requests);
        Assert.Null(result.StemSeparationWarning);
    }

    [Fact]
    public async Task RunStemSeparationAsync_replaces_existing_vocals_and_ambiance_records()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);
        ProjectArtifact initialVocals = Assert.Single(created.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);
        ProjectArtifact initialAmbiance = Assert.Single(created.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Ambiance);
        ProjectArtifact initialMusic = Assert.Single(created.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Music);
        ProjectArtifact initialSfx = Assert.Single(created.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.SoundEffects);
        ProjectArtifact initialAnalysis = Assert.Single(created.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.AudioQualityAnalysis);

        TranscriptProjectState rerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);

        ProjectArtifact rerunVocals = Assert.Single(rerun.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);
        ProjectArtifact rerunAmbiance = Assert.Single(rerun.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Ambiance);
        ProjectArtifact rerunMusic = Assert.Single(rerun.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Music);
        ProjectArtifact rerunSfx = Assert.Single(rerun.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.SoundEffects);
        ProjectArtifact rerunAnalysis = rerun.ProjectState.Artifacts
            .Where(artifact => artifact.Kind == ArtifactKind.AudioQualityAnalysis)
            .OrderByDescending(artifact => artifact.CreatedAtUtc)
            .First();
        Assert.Equal(initialVocals.Id, rerunVocals.Id);
        Assert.Equal(initialAmbiance.Id, rerunAmbiance.Id);
        Assert.Equal(initialMusic.Id, rerunMusic.Id);
        Assert.Equal(initialSfx.Id, rerunSfx.Id);
        Assert.NotEqual(initialAnalysis.RelativePath, rerunAnalysis.RelativePath);
        Assert.NotEqual(initialVocals.RelativePath, rerunVocals.RelativePath);
        Assert.NotEqual(initialAmbiance.RelativePath, rerunAmbiance.RelativePath);
        Assert.NotEqual(initialMusic.RelativePath, rerunMusic.RelativePath);
        Assert.NotEqual(initialSfx.RelativePath, rerunSfx.RelativePath);
        Assert.Equal(rerunVocals.RelativePath, rerun.AsrAudioRelativePath);
        Assert.Equal(created.CurrentTranscriptRevision!.RevisionNumber + 1, rerun.CurrentTranscriptRevision!.RevisionNumber);
        Assert.Equal(2, scope.StemSeparationEngine.CallCount);
    }

    [Fact]
    public async Task RunStemSeparationAsync_regenerates_after_previous_generated_rerun()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        TranscriptProjectState firstRerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);
        TranscriptProjectState secondRerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);

        Assert.NotNull(created.CurrentTranscriptRevision!.StageRunId);
        Assert.NotNull(firstRerun.CurrentTranscriptRevision!.StageRunId);
        Assert.NotNull(secondRerun.CurrentTranscriptRevision!.StageRunId);
        Assert.Equal(firstRerun.CurrentTranscriptRevision.RevisionNumber + 1, secondRerun.CurrentTranscriptRevision.RevisionNumber);
        Assert.Equal(3, scope.StemSeparationEngine.CallCount);
    }

    [Fact]
    public async Task RunStemSeparationAsync_autodetects_language_for_regenerated_asr()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(tempDirectory, transcriptionEngine: transcriptionEngine);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("es"), TestContext.Current.CancellationToken);

        TranscriptProjectState rerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);

        Assert.Null(transcriptionEngine.LastSourceLanguage);
        Assert.Equal(created.CurrentTranscriptRevision!.RevisionNumber + 1, rerun.CurrentTranscriptRevision!.RevisionNumber);
    }

    [Fact]
    public async Task RunTranscriptStageAsync_passes_requested_source_language_to_asr()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(tempDirectory, transcriptionEngine: transcriptionEngine);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        await scope.Workspace.RunTranscriptStageAsync(
            StageNames.Asr,
            enableSpeakerDiarization: false,
            modelPreferences: null,
            cancellationToken: TestContext.Current.CancellationToken,
            sourceLanguage: "fr");

        Assert.Equal("fr", transcriptionEngine.LastSourceLanguage);
    }

    [Fact]
    public async Task RunStemSeparationAsync_regenerateTranscriptFalse_skips_transcript_regeneration_when_assignments_missing()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        TranscriptProjectState rerun = await scope.Service.RunStemSeparationAsync(
            TestContext.Current.CancellationToken,
            regenerateTranscript: false);

        Assert.Equal(created.CurrentTranscriptRevision!.Id, rerun.CurrentTranscriptRevision!.Id);
        Assert.Equal(created.CurrentTranscriptRevision.RevisionNumber, rerun.CurrentTranscriptRevision.RevisionNumber);
        Assert.Equal(2, scope.StemSeparationEngine.CallCount);
    }

    [Fact]
    public async Task RunStemSeparationAsync_preserves_existing_diarized_assignments()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var diarizationEngine = new RecordingDiarizationEngine(
        [
            new DiarizedSpeakerTurn("spk_0", 0d, 5.8d, Confidence: 0.9d, HasOverlap: false),
            new DiarizedSpeakerTurn("spk_1", 6d, 11.8d, Confidence: 0.8d, HasOverlap: false)
        ]);
        FakeServiceScope scope = CreateScope(tempDirectory, diarizationEngine: diarizationEngine);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: true,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);
        Guid[] createdSpeakerIdsBySegment = created.TranscriptSegments
            .OrderBy(segment => segment.SegmentIndex)
            .Select(segment => segment.SpeakerId ?? Guid.Empty)
            .ToArray();

        TranscriptProjectState rerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);

        Assert.NotEmpty(created.SpeakerTurns);
        Assert.Equal(1, diarizationEngine.CallCount);
        Assert.Equal(created.CurrentTranscriptRevision!.Id, rerun.CurrentTranscriptRevision!.Id);
        Assert.Equal(created.CurrentTranscriptRevision.RevisionNumber, rerun.CurrentTranscriptRevision.RevisionNumber);
        Assert.Equal(
            createdSpeakerIdsBySegment,
            rerun.TranscriptSegments
                .OrderBy(segment => segment.SegmentIndex)
                .Select(segment => segment.SpeakerId ?? Guid.Empty)
                .ToArray());
        Assert.Equal(2, scope.StemSeparationEngine.CallCount);
    }

    [Fact]
    public async Task RunStemSeparationAsync_retries_diarization_when_previous_run_had_no_turns()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var diarizationEngine = new SequencedDiarizationEngine(
            [],
            [
                new DiarizedSpeakerTurn("spk_0", 0d, 5.8d, Confidence: 0.9d, HasOverlap: false),
                new DiarizedSpeakerTurn("spk_1", 6d, 11.8d, Confidence: 0.8d, HasOverlap: false)
            ]);
        FakeServiceScope scope = CreateScope(tempDirectory, diarizationEngine: diarizationEngine);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: true,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        Assert.Empty(created.SpeakerTurns);
        Assert.Equal(1, diarizationEngine.CallCount);

        TranscriptProjectState rerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);

        Assert.Equal(2, diarizationEngine.CallCount);
        Assert.Equal(2, rerun.SpeakerTurns.Count);
        Assert.Equal(created.CurrentTranscriptRevision!.RevisionNumber + 1, rerun.CurrentTranscriptRevision!.RevisionNumber);
        Assert.Contains(rerun.Speakers, speaker => speaker.DisplayName == "Speaker 1");
        Assert.Contains(rerun.Speakers, speaker => speaker.DisplayName == "Speaker 2");
        Assert.Contains(rerun.Speakers, speaker => speaker.DisplayName == "Speaker 3");
        Assert.Equal(
            2,
            rerun.TranscriptSegments
                .Select(segment => segment.SpeakerId)
                .Where(speakerId => speakerId is not null)
                .Distinct()
                .Count());
    }

    [Fact]
    public async Task RunStemSeparationAsync_when_latest_audio_prep_fails_keeps_latest_raw_route()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var analyzer = new FakeAudioQualityAnalyzer();
        analyzer.QueueResult(new AudioQualityAnalysisResult(
            "full-mix-create.wav",
            CreateAudioQualityMetrics(SpeechAudioSourceKind.FullMix),
            AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.FullMix),
            [],
            []));
        analyzer.QueueResult(new AudioQualityAnalysisResult(
            "vocals-create.wav",
            CreateAudioQualityMetrics(SpeechAudioSourceKind.VocalStem) with { ActiveRmsDbfs = -60.0d },
            AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.VocalStem),
            [AudioQualityDefectKind.NearSilence],
            []));
        analyzer.QueueResult(new AudioQualityAnalysisResult(
            "full-mix-rerun.wav",
            CreateAudioQualityMetrics(SpeechAudioSourceKind.FullMix),
            AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.FullMix),
            [],
            []));
        analyzer.QueueResult(new AudioQualityAnalysisResult(
            "vocals-rerun.wav",
            CreateAudioQualityMetrics(SpeechAudioSourceKind.VocalStem),
            AudioQualityAnalysisThresholds.ForSource(SpeechAudioSourceKind.VocalStem),
            [AudioQualityDefectKind.LowVolume],
            []));

        var processingService = new FakeSpeechAudioProcessingService { ThrowOnCallNumber = 1 };
        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: transcriptionEngine,
            audioQualityAnalyzer: analyzer,
            speechAudioProcessingService: processingService);

        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, created.AsrAudioRelativePath);

        TranscriptProjectState rerun = await scope.Service.RunStemSeparationAsync(TestContext.Current.CancellationToken);

        ProjectArtifact rerunVocals = Assert.Single(rerun.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);
        Assert.Equal(rerunVocals.RelativePath, rerun.AsrAudioRelativePath);
        Assert.Equal(scope.ArtifactStore.GetPath(rerunVocals.RelativePath), transcriptionEngine.LastAudioPath);
        Assert.Contains(rerun.StageRuns, stageRun => stageRun.StageName == StageNames.AudioPreparation && stageRun.Status == StageRunStatus.Failed);
    }

    [Fact]
    public async Task CreateAsync_when_stem_separation_fails_records_failure_and_keeps_full_mix_route()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var transcriptionEngine = new RecordingAudioTranscriptionEngine();
        var stemEngine = new FakeStemSeparationEngine { ThrowOnSeparate = true };
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: transcriptionEngine,
            stemSeparationEngine: stemEngine);

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: true,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        Assert.DoesNotContain(result.ProjectState.Artifacts, artifact => artifact.Kind is ArtifactKind.Vocals or ArtifactKind.Ambiance);
        ProjectArtifact analysis = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.AudioQualityAnalysis);
        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, result.AsrAudioRelativePath);
        Assert.Equal(scope.ArtifactStore.GetPath(ProjectArtifactPaths.NormalizedAudioRelativePath), transcriptionEngine.LastAudioPath);
        Assert.NotNull(analysis);
        Assert.Contains(result.StageRuns, stageRun => stageRun.StageName == "separation" && stageRun.Status == StageRunStatus.Failed);
        Assert.Contains(result.StageRuns, stageRun => stageRun.StageName == StageNames.AudioPreparation && stageRun.Status == StageRunStatus.Completed);
        Assert.Equal("Dialogue isolation model unavailable; no clean ambiance track was generated.", result.StemSeparationWarning);
        ProjectArtifact degradation = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.PipelineDegradation);
        Assert.Equal("DIALOGUE_ISOLATION_UNAVAILABLE", degradation.DegradationCode);
    }

    [Fact]
    public async Task OpenAsync_with_demucs_v4_stems_warns_as_legacy_and_uses_normalized_audio_routes()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        var stemEngine = new FakeStemSeparationEngine
        {
            EngineFamily = "demucs-v4",
            Model = "demucs-v4",
            RawStemNames = ["drums", "bass", "other", "vocals"],
            WriteSoundEffects = false
        };
        FakeServiceScope scope = CreateScope(tempDirectory, stemSeparationEngine: stemEngine);

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest(
                "Transcript Demo",
                sourcePath,
                EnableSpeakerDiarization: false,
                EnableStemSeparation: true),
            TestContext.Current.CancellationToken);

        ProjectArtifact vocals = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Vocals);
        ProjectArtifact ambiance = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Ambiance);
        ProjectArtifact music = Assert.Single(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.Music);

        Assert.Contains("older/non-current separator", result.StemSeparationWarning, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, result.AsrAudioRelativePath);
        Assert.Equal(ProjectArtifactPaths.NormalizedAudioRelativePath, result.MixSourceAudioRelativePath);
        Assert.Contains("/demucs-v4/", vocals.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/demucs-v4/", ambiance.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("/demucs-v4/", music.RelativePath, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(result.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.SoundEffects);
        Assert.Contains("generated-demucs-v4-vocals", vocals.Provenance, StringComparison.Ordinal);
        Assert.Contains("engine_family=demucs-v4", vocals.Provenance, StringComparison.Ordinal);
    }

}
