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
    public void TranscriptConfidenceEvaluator_flags_low_confidence_at_configured_threshold()
    {
        TranscriptRevision revision = TranscriptRevision.Create(Guid.NewGuid(), stageRunId: null, revisionNumber: 1, DateTimeOffset.UtcNow);
        TranscriptSegment segment = TranscriptSegment.Create(
            revision.Id,
            0,
            0d,
            2d,
            "Hello world",
            words:
            [
                TranscriptWord.Create(0, 0d, 1d, "Hello", 0.92d),
                TranscriptWord.Create(1, 1d, 2d, "world", 0.60d)
            ]);

        TranscriptConfidenceAssessment defaultThreshold = TranscriptConfidenceEvaluator.Assess(segment, 0.75d);
        TranscriptConfidenceAssessment lowerThreshold = TranscriptConfidenceEvaluator.Assess(segment, 0.50d);

        Assert.Equal(TranscriptConfidenceLevel.Low, defaultThreshold.Level);
        Assert.True(defaultThreshold.ReviewRecommended);
        Assert.Equal(TranscriptConfidenceLevel.Medium, lowerThreshold.Level);
        Assert.False(lowerThreshold.ReviewRecommended);
    }

    [Fact]
    public async Task CreateAsync_when_detected_language_votes_are_mixed_uses_plurality_winner()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: new FixedAudioTranscriptionEngine(
            [
                new RecognizedTranscriptSegment(0, 0.0, 5.8, "Hello.", DetectedLanguage: "en"),
                new RecognizedTranscriptSegment(1, 6.0, 11.8, "Hola.", DetectedLanguage: "es")
            ]));

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", result.TranscriptLanguage);
    }

    [Fact]
    public async Task CreateAsync_when_supported_detected_language_has_majority_persists_transcript_language()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: new FixedAudioTranscriptionEngine(
            [
                new RecognizedTranscriptSegment(0, 0.0, 3.0, "Hello.", DetectedLanguage: "en"),
                new RecognizedTranscriptSegment(1, 3.0, 6.0, "There.", DetectedLanguage: "en"),
                new RecognizedTranscriptSegment(2, 6.0, 9.0, "Hola.", DetectedLanguage: "es")
            ]));

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", result.TranscriptLanguage);
    }

    [Fact]
    public async Task CreateAsync_when_detected_language_has_no_configured_routes_persists_language_without_targets()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(
            tempDirectory,
            transcriptionEngine: new FixedAudioTranscriptionEngine(
            [
                new RecognizedTranscriptSegment(0, 0.0, 5.8, "Γεια σου.", DetectedLanguage: "el"),
                new RecognizedTranscriptSegment(1, 6.0, 11.8, "Ευχαριστώ.", DetectedLanguage: "el")
            ]));

        TranscriptProjectState result = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.Equal("el", result.TranscriptLanguage);
        Assert.Empty(result.SupportedTargetLanguages);
        Assert.All(result.TranscriptSegments, segment => Assert.Equal("el", segment.DetectedLanguage));
    }

    [Fact]
    public async Task SaveEditsAsync_creates_new_revision_without_overwriting_generated_revision()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        TranscriptProjectState saved = await scope.Service.SaveEditsAsync(
            new SaveTranscriptEditsRequest(
                created.CurrentTranscriptRevision!.Id,
                [new EditedTranscriptSegment(created.TranscriptSegments[0].Id, "Edited segment text.", created.TranscriptSegments[0].SpeakerId)]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(saved.CurrentTranscriptRevision);
        Assert.Equal(2, saved.CurrentTranscriptRevision!.RevisionNumber);
        Assert.Equal("Edited segment text.", saved.TranscriptSegments[0].Text);

        TranscriptRevision originalRevision = Assert.Single(scope.TranscriptRepository.Revisions, revision => revision.RevisionNumber == 1);
        IReadOnlyList<TranscriptSegment> originalSegments = scope.TranscriptRepository.SegmentsByRevisionId[originalRevision.Id];
        Assert.Equal("Generated segment 1.", originalSegments[0].Text);
    }

    [Fact]
    public async Task SplitSegmentAsync_creates_two_segments_covering_original_duration()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        TranscriptProjectState split = await scope.Service.SplitSegmentAsync(
            new SplitTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 2.9),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, split.TranscriptSegments.Count);
        Assert.Equal(0.0, split.TranscriptSegments[0].StartSeconds, 3);
        Assert.Equal(2.9, split.TranscriptSegments[0].EndSeconds, 3);
        Assert.Equal(2.9, split.TranscriptSegments[1].StartSeconds, 3);
        Assert.Equal(5.8, split.TranscriptSegments[1].EndSeconds, 3);
    }

    [Fact]
    public async Task TrimSegmentAsync_when_end_is_reduced_preserves_removed_tail_as_new_segment()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        scope.SpeechRegionDetector.SetRegions(new SpeechRegion(0, 0d, 12d));
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        TranscriptProjectState trimmed = await scope.Service.TrimSegmentAsync(
            new TrimTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 0d, 2d),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, trimmed.TranscriptSegments.Count);
        Assert.Equal((0d, 2d), (trimmed.TranscriptSegments[0].StartSeconds, trimmed.TranscriptSegments[0].EndSeconds));
        Assert.Equal((2d, 12d), (trimmed.TranscriptSegments[1].StartSeconds, trimmed.TranscriptSegments[1].EndSeconds));
        Assert.Equal(created.TranscriptSegments[0].SpeakerId, trimmed.TranscriptSegments[0].SpeakerId);
        Assert.Equal(created.TranscriptSegments[0].SpeakerId, trimmed.TranscriptSegments[1].SpeakerId);
        Assert.Equal("Generated", trimmed.TranscriptSegments[0].Text);
        Assert.Equal("segment 1.", trimmed.TranscriptSegments[1].Text);
    }

    [Fact]
    public async Task TrimSegmentAsync_when_start_is_increased_preserves_removed_head_as_new_segment()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        scope.SpeechRegionDetector.SetRegions(new SpeechRegion(0, 0d, 12d));
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        TranscriptProjectState trimmed = await scope.Service.TrimSegmentAsync(
            new TrimTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 3d, 12d),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, trimmed.TranscriptSegments.Count);
        Assert.Equal((0d, 3d), (trimmed.TranscriptSegments[0].StartSeconds, trimmed.TranscriptSegments[0].EndSeconds));
        Assert.Equal((3d, 12d), (trimmed.TranscriptSegments[1].StartSeconds, trimmed.TranscriptSegments[1].EndSeconds));
        Assert.Equal("Generated", trimmed.TranscriptSegments[0].Text);
        Assert.Equal("segment 1.", trimmed.TranscriptSegments[1].Text);
    }

    [Fact]
    public async Task TrimSegmentAsync_when_start_and_end_shrink_creates_three_segments()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        scope.SpeechRegionDetector.SetRegions(new SpeechRegion(0, 0d, 12d));
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        TranscriptProjectState trimmed = await scope.Service.TrimSegmentAsync(
            new TrimTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 4d, 8d),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, trimmed.TranscriptSegments.Count);
        Assert.Equal((0d, 4d), (trimmed.TranscriptSegments[0].StartSeconds, trimmed.TranscriptSegments[0].EndSeconds));
        Assert.Equal((4d, 8d), (trimmed.TranscriptSegments[1].StartSeconds, trimmed.TranscriptSegments[1].EndSeconds));
        Assert.Equal((8d, 12d), (trimmed.TranscriptSegments[2].StartSeconds, trimmed.TranscriptSegments[2].EndSeconds));
        Assert.Equal("Generated", trimmed.TranscriptSegments[0].Text);
        Assert.Equal("segment", trimmed.TranscriptSegments[1].Text);
        Assert.Equal("1.", trimmed.TranscriptSegments[2].Text);
        Assert.Single(trimmed.TranscriptSegments[0].Words);
        Assert.Single(trimmed.TranscriptSegments[1].Words);
        Assert.Single(trimmed.TranscriptSegments[2].Words);
    }

    [Fact]
    public async Task TrimSegmentAsync_does_not_replace_empty_character_slice_with_full_text()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        scope.SpeechRegionDetector.SetRegions(new SpeechRegion(0, 0d, 12d));
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        TranscriptProjectState edited = await scope.Service.SaveEditsAsync(
            new SaveTranscriptEditsRequest(
                created.CurrentTranscriptRevision!.Id,
                [new EditedTranscriptSegment(created.TranscriptSegments[0].Id, "A B", created.TranscriptSegments[0].SpeakerId)]),
            TestContext.Current.CancellationToken);

        TranscriptProjectState trimmed = await scope.Service.TrimSegmentAsync(
            new TrimTranscriptSegmentRequest(edited.CurrentTranscriptRevision!.Id, edited.TranscriptSegments[0].Id, 4d, 8d),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, trimmed.TranscriptSegments.Count);
        Assert.All(trimmed.TranscriptSegments, segment => Assert.False(string.IsNullOrWhiteSpace(segment.Text)));
        Assert.DoesNotContain(trimmed.TranscriptSegments, segment => segment.Text == "A B");
    }

    [Fact]
    public async Task TrimSegmentAsync_marks_existing_translation_and_tts_stale()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        scope.SpeechRegionDetector.SetRegions(new SpeechRegion(0, 0d, 12d));
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

        TranscriptProjectState trimmed = await scope.Service.TrimSegmentAsync(
            new TrimTranscriptSegmentRequest(translated.CurrentTranscriptRevision!.Id, translated.TranscriptSegments[0].Id, 0d, 2d),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.True(take.IsStale);
        Assert.True(trimmed.IsTranslationStale);
        Assert.All(trimmed.TranscriptSegments, segment => Assert.Contains(segment.SegmentIndex, trimmed.StaleTranslatedSegmentIndices));
        Assert.Contains(trimmed.TtsSegmentStates, state => state.SegmentIndex == 0 && state.IsStale);
    }

    [Fact]
    public async Task TrimSegmentAsync_when_segment_count_is_unchanged_marks_only_edited_tts_take_stale()
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
                translated.TranscriptSegments[0].Id),
            TestContext.Current.CancellationToken);
        await scope.Service.GenerateTtsForSegmentAsync(
            new GenerateTtsForSegmentRequest(
                translated.CurrentTranscriptRevision!.Id,
                translated.TranscriptSegments[1].Id),
            TestContext.Current.CancellationToken);

        TranscriptSegment firstSegment = translated.TranscriptSegments[0];
        TranscriptProjectState trimmed = await scope.Service.TrimSegmentAsync(
            new TrimTranscriptSegmentRequest(
                translated.CurrentTranscriptRevision!.Id,
                firstSegment.Id,
                firstSegment.StartSeconds,
                firstSegment.EndSeconds + 0.1d),
            TestContext.Current.CancellationToken);

        Assert.Equal(translated.TranscriptSegments.Count, trimmed.TranscriptSegments.Count);
        TtsTake[] takes = scope.TtsTakeRepository.Takes.OrderBy(take => take.SegmentIndex).ToArray();
        Assert.Collection(
            takes,
            take =>
            {
                Assert.Equal(0, take.SegmentIndex);
                Assert.True(take.IsStale);
            },
            take =>
            {
                Assert.Equal(1, take.SegmentIndex);
                Assert.False(take.IsStale);
            });
        Assert.Contains(trimmed.TtsSegmentStates, state => state.SegmentIndex == 0 && state.IsStale);
        Assert.Contains(trimmed.TtsSegmentStates, state => state.SegmentIndex == 1 && !state.IsStale);
    }

    [Fact]
    public async Task DeleteSegmentAsync_marks_shifted_tts_takes_stale()
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

        await scope.Service.DeleteSegmentAsync(
            new DeleteTranscriptSegmentRequest(
                translated.CurrentTranscriptRevision!.Id,
                translated.TranscriptSegments[0].Id),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(1, take.SegmentIndex);
        Assert.True(take.IsStale);
        Assert.Equal(TtsTakeStatus.Stale, take.Status);
    }

    [Fact]
    public async Task RestoreEditingStateAsync_restores_previous_transcript_revision()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        TranscriptProjectState split = await scope.Service.SplitSegmentAsync(
            new SplitTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 2.9),
            TestContext.Current.CancellationToken);

        TranscriptProjectState restored = await scope.Service.RestoreEditingStateAsync(
            new RestoreEditingStateRequest(
                created.SelectedTranslationTargetLanguage,
                created.TranscriptSegments,
                created.CurrentTranslationRevision is null ? null : created.TranslatedSegments,
                created.Speakers.ToDictionary(speaker => speaker.Id, speaker => speaker.DisplayName),
                created.VoiceAssignments),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, split.TranscriptSegments.Count);
        Assert.Equal(2, restored.TranscriptSegments.Count);
        Assert.Equal(created.TranscriptSegments[0].StartSeconds, restored.TranscriptSegments[0].StartSeconds);
        Assert.Equal(created.TranscriptSegments[0].EndSeconds, restored.TranscriptSegments[0].EndSeconds);
        Assert.Equal(3, restored.CurrentTranscriptRevision!.RevisionNumber);
    }

    [Fact]
    public async Task MergeSegmentsAsync_creates_single_segment_spanning_selected_pair()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);

        TranscriptProjectState merged = await scope.Service.MergeSegmentsAsync(
            new MergeTranscriptSegmentsRequest(
                created.CurrentTranscriptRevision!.Id,
                created.TranscriptSegments[0].Id,
                created.TranscriptSegments[1].Id),
            TestContext.Current.CancellationToken);

        TranscriptSegment mergedSegment = Assert.Single(merged.TranscriptSegments);
        Assert.Equal(0.0, mergedSegment.StartSeconds, 3);
        Assert.Equal(11.8, mergedSegment.EndSeconds, 3);
        Assert.Contains("Generated segment 1.", mergedSegment.Text);
        Assert.Contains("Generated segment 2.", mergedSegment.Text);
    }

    [Fact]
    public async Task MergeSegmentRunAsync_marks_shifted_tts_takes_stale()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        await scope.Service.SplitSegmentAsync(
            new SplitTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 2.9),
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
                translated.TranscriptSegments[2].Id),
            TestContext.Current.CancellationToken);

        await scope.Service.MergeSegmentRunAsync(
            new MergeTranscriptSegmentRunRequest(
                translated.CurrentTranscriptRevision!.Id,
                [translated.TranscriptSegments[0].Id, translated.TranscriptSegments[1].Id]),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.Equal(2, take.SegmentIndex);
        Assert.True(take.IsStale);
        Assert.Equal(TtsTakeStatus.Stale, take.Status);
    }

    [Fact]
    public async Task MergeSegmentRunAsync_creates_single_segment_spanning_adjacent_same_speaker_run()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        TranscriptProjectState split = await scope.Service.SplitSegmentAsync(
            new SplitTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 2.9),
            TestContext.Current.CancellationToken);

        TranscriptProjectState merged = await scope.Service.MergeSegmentRunAsync(
            new MergeTranscriptSegmentRunRequest(
                split.CurrentTranscriptRevision!.Id,
                split.TranscriptSegments.Select(segment => segment.Id).ToArray()),
            TestContext.Current.CancellationToken);

        TranscriptSegment mergedSegment = Assert.Single(merged.TranscriptSegments);
        Assert.Equal(0.0, mergedSegment.StartSeconds, 3);
        Assert.Equal(11.8, mergedSegment.EndSeconds, 3);
        Assert.Equal(created.Speakers[0].Id, mergedSegment.SpeakerId);
    }

    [Fact]
    public async Task MergeSegmentRunAsync_rejects_non_adjacent_segments()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath, EnableSpeakerDiarization: false),
            TestContext.Current.CancellationToken);
        TranscriptProjectState split = await scope.Service.SplitSegmentAsync(
            new SplitTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 2.9),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service.MergeSegmentRunAsync(
                new MergeTranscriptSegmentRunRequest(
                    split.CurrentTranscriptRevision!.Id,
                    [split.TranscriptSegments[0].Id, split.TranscriptSegments[2].Id]),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task MergeSegmentRunAsync_rejects_mixed_speaker_segments()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service.MergeSegmentRunAsync(
                new MergeTranscriptSegmentRunRequest(
                    created.CurrentTranscriptRevision!.Id,
                    created.TranscriptSegments.Select(segment => segment.Id).ToArray()),
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task TrimSegmentAsync_rejects_overlap_with_adjacent_segment()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            scope.Service.TrimSegmentAsync(
                new TrimTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 0.0, 6.5),
                TestContext.Current.CancellationToken));
    }

}
