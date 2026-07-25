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
    public async Task GenerateTranslationAsync_creates_translation_revision_and_transcript_edits_mark_it_needing_refresh()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        TranscriptProjectState languageSet = await scope.Service.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest("en"),
            TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", languageSet.TranscriptLanguage);
        Assert.NotNull(translated.CurrentTranslationRevision);
        Assert.Equal(1, translated.CurrentTranslationRevision!.RevisionNumber);
        Assert.Equal("opus-mt", translated.CurrentTranslationRevision.TranslationProvider);
        Assert.Equal("fake-opus-en-es", translated.CurrentTranslationRevision.ModelId);
        Assert.False(translated.IsTranslationStale);
        Assert.Equal(2, translated.TranslatedSegments.Count);
        Assert.Equal("Segmento generado 1.", translated.TranslatedSegments[0].Text);
        Assert.All(translated.TranslatedSegments, segment => Assert.False(string.IsNullOrWhiteSpace(segment.SourceSegmentHash)));
        Assert.Contains(translated.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.TranslationRevision);
        Assert.Contains(translated.StageRuns, stageRun => stageRun.StageName == "translation" && stageRun.Status == StageRunStatus.Completed);

        TranscriptProjectState transcriptEdited = await scope.Service.SaveEditsAsync(
            new SaveTranscriptEditsRequest(
                translated.CurrentTranscriptRevision!.Id,
                [new EditedTranscriptSegment(translated.TranscriptSegments[0].Id, "Updated transcript text.", translated.TranscriptSegments[0].SpeakerId)]),
            TestContext.Current.CancellationToken);

        Assert.True(transcriptEdited.IsTranslationStale);
        Assert.NotNull(transcriptEdited.CurrentTranslationRevision);
        Assert.Equal(1, transcriptEdited.CurrentTranslationRevision!.RevisionNumber);
        Assert.Contains(0, transcriptEdited.StaleTranslatedSegmentIndices);
    }

    [Fact]
    public async Task GenerateTranslationAsync_supports_spanish_to_english()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        await scope.Service.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest("es"),
            TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("es", "en"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(translated.CurrentTranslationRevision);
        Assert.Equal("en", translated.CurrentTranslationRevision!.TargetLanguage);
        Assert.False(translated.IsTranslationStale);
        Assert.Equal("Generated translation 1.", translated.TranslatedSegments[0].Text);
        Assert.Contains(translated.StageRuns, stageRun => stageRun.StageName == "translation" && stageRun.Status == StageRunStatus.Completed);
    }

    [Fact]
    public async Task SaveTranslationEditsAsync_creates_new_revision_without_overwriting_generated_translation()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        await scope.Service.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Service.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState saved = await scope.Service.SaveTranslationEditsAsync(
            new SaveTranslationEditsRequest(
                translated.CurrentTranslationRevision!.Id,
                "es",
                [new EditedTranslatedSegment(0, "Traduccion editada.")]),
            TestContext.Current.CancellationToken);

        Assert.NotNull(saved.CurrentTranslationRevision);
        Assert.Equal(2, saved.CurrentTranslationRevision!.RevisionNumber);
        Assert.Equal("Traduccion editada.", saved.TranslatedSegments[0].Text);

        TranslationRevision generatedRevision = Assert.Single(scope.TranslationRepository.Revisions, revision => revision.RevisionNumber == 1);
        TranslationRevision editedRevision = Assert.Single(scope.TranslationRepository.Revisions, revision => revision.RevisionNumber == 2);
        Assert.Equal(generatedRevision.SourceTranscriptRevisionId, editedRevision.SourceTranscriptRevisionId);

        IReadOnlyList<TranslatedSegment> generatedSegments = scope.TranslationRepository.SegmentsByRevisionId[generatedRevision.Id];
        Assert.Equal("Segmento generado 1.", generatedSegments[0].Text);
    }
    [Fact]
    public async Task SaveTranslationEditsAsync_marks_existing_tts_take_stale()
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
        await scope.Service.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);
        await scope.Service.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        TranscriptProjectState edited = await scope.Service.SaveTranslationEditsAsync(
            new SaveTranslationEditsRequest(
                translated.CurrentTranslationRevision!.Id,
                "es",
                [new EditedTranslatedSegment(0, "Traduccion editada.")]),
            TestContext.Current.CancellationToken);

        TtsTake take = Assert.Single(scope.TtsTakeRepository.Takes);
        Assert.True(take.IsStale);
        Assert.Equal(TtsTakeStatus.Stale, take.Status);
        TtsSegmentState segmentState = Assert.Single(edited.TtsSegmentStates, state => state.SegmentIndex == 0);
        Assert.True(segmentState.IsStale);
    }

    [Fact]
    public async Task RetranslateSegmentAsync_preserves_other_segments_and_marks_changed_take_stale()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var translationEngine = new FakeTranslationEngine((request, segment) =>
            request.Segments.Count == 1
                ? $"Retraducido {segment.Index + 1}."
                : $"Segmento generado {segment.Index + 1}.");

        FakeServiceScope scope = CreateScope(tempDirectory, translationEngine: translationEngine);
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

        TranscriptProjectState retranslated = await scope.Service.RetranslateSegmentAsync(
            new RetranslateSegmentRequest(
                translated.CurrentTranslationRevision!.Id,
                translated.TranscriptSegments[1].Id,
                "en",
                "es"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(retranslated.CurrentTranslationRevision);
        Assert.Equal(2, retranslated.CurrentTranslationRevision!.RevisionNumber);
        Assert.Equal("Segmento generado 1.", retranslated.TranslatedSegments[0].Text);
        Assert.Equal("Retraducido 2.", retranslated.TranslatedSegments[1].Text);

        TtsTake firstTake = Assert.Single(scope.TtsTakeRepository.Takes, take => take.SegmentIndex == 0);
        TtsTake secondTake = Assert.Single(scope.TtsTakeRepository.Takes, take => take.SegmentIndex == 1);
        Assert.False(firstTake.IsStale);
        Assert.True(secondTake.IsStale);
    }
    [Fact]
    public async Task SelectTranslationTargetAsync_switches_to_supported_pivot_target_and_reports_route_status()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        TranscriptProjectState transcriptLanguageSet = await scope.Service.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest("en"),
            TestContext.Current.CancellationToken);

        Assert.Contains(transcriptLanguageSet.SupportedTargetLanguages, option => option.LanguageCode == "fr" && option.RoutingKind == TranslationRoutingKind.Pivot);

        TranscriptProjectState frenchTarget = await scope.Service.SelectTranslationTargetAsync(
            new SetTranslationTargetRequest("fr"),
            TestContext.Current.CancellationToken);

        Assert.Equal("fr", frenchTarget.SelectedTranslationTargetLanguage);
        Assert.Null(frenchTarget.CurrentTranslationRevision);
        Assert.Contains(frenchTarget.SupportedTargetLanguages, option => option.LanguageCode == "fr" && option.IsAvailable);

        TranscriptProjectState reopened = await scope.Service.OpenAsync(TestContext.Current.CancellationToken);
        Assert.Equal("fr", reopened.SelectedTranslationTargetLanguage);
    }

    [Fact]
    public async Task SetTranscriptLanguageAsync_normalizes_regional_codes_with_underscores()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        TranscriptProjectState updated = await scope.Service.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest("en_US"),
            TestContext.Current.CancellationToken);

        Assert.Equal("en", updated.TranscriptLanguage);
    }
    [Fact]
    public async Task SelectTranslationTargetAsync_when_source_language_is_unknown_preserves_requested_target()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        scope.ArtifactStore.Remove(ProjectArtifactPaths.ManifestRelativePath);
        TranscriptProjectState selected = await scope.Service.SelectTranslationTargetAsync(
            new SetTranslationTargetRequest("fr"),
            TestContext.Current.CancellationToken);

        Assert.Null(selected.TranscriptLanguage);
        Assert.Empty(selected.SupportedTargetLanguages);
        Assert.Equal("fr", selected.SelectedTranslationTargetLanguage);
    }
}
