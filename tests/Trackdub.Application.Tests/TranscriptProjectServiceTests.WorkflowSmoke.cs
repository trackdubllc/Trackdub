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
    public async Task ProjectWorkflow_CreateAsync_returns_refreshed_state()
    {
        var (_, state) = await CreateWorkspaceProjectAsync();

        Assert.NotNull(state.CurrentTranscriptRevision);
        Assert.Equal(1, state.CurrentTranscriptRevision!.RevisionNumber);
        Assert.Equal(2, state.TranscriptSegments.Count);
    }

    [Fact]
    public async Task ProjectWorkflow_RelocateSourceAsync_preserves_selected_translation_target()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Workspace.Project.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        await scope.Workspace.Translation.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest("en"),
            TestContext.Current.CancellationToken);
        await scope.Workspace.Translation.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "fr"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState relocated = await scope.Workspace.Project.RelocateSourceAsync(
            new RelocateTranscriptSourceRequest(sourcePath, "fr"),
            TestContext.Current.CancellationToken);

        Assert.Equal("fr", relocated.SelectedTranslationTargetLanguage);
        Assert.NotNull(relocated.CurrentTranslationRevision);
        Assert.Equal("fr", relocated.CurrentTranslationRevision!.TargetLanguage);
        Assert.Equal(2, relocated.TranslatedSegments.Count);
    }

    [Fact]
    public async Task ProjectWorkflow_RenameProjectAsync_persists_project_name_and_manifest()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Workspace.Project.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);
        await scope.Workspace.Translation.SetTranscriptLanguageAsync(
            new SetTranscriptLanguageRequest("en"),
            TestContext.Current.CancellationToken);
        await scope.Workspace.Translation.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "fr"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState renamed = await scope.Workspace.Project.RenameProjectAsync(
            new RenameProjectRequest("Renamed Demo", "fr"),
            TestContext.Current.CancellationToken);
        ProjectManifest? manifest = await scope.ArtifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            TestContext.Current.CancellationToken);

        Assert.Equal("Renamed Demo", renamed.ProjectState.Project.Name);
        Assert.Equal("fr", renamed.SelectedTranslationTargetLanguage);
        Assert.NotNull(renamed.CurrentTranslationRevision);
        Assert.NotNull(manifest);
        Assert.Equal("Renamed Demo", manifest!.Name);

        TranscriptProjectState current = await scope.Workspace.Project.OpenAsync(TestContext.Current.CancellationToken);
        TranscriptProjectState renamedAgain = await scope.Workspace.Project.RenameProjectAsync(
            new RenameProjectRequest("Renamed Demo Again"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Renamed Demo Again", renamedAgain.ProjectState.Project.Name);
        Assert.Equal(current.SelectedTranslationTargetLanguage, renamedAgain.SelectedTranslationTargetLanguage);
    }

    [Fact]
    public async Task TranscriptWorkflow_SaveEditsAsync_returns_refreshed_state()
    {
        var (scope, created) = await CreateWorkspaceProjectAsync();

        TranscriptProjectState saved = await scope.Workspace.Transcript.SaveEditsAsync(
            new SaveTranscriptEditsRequest(
                created.CurrentTranscriptRevision!.Id,
                [new EditedTranscriptSegment(created.TranscriptSegments[0].Id, "Workspace edited text.", created.TranscriptSegments[0].SpeakerId)]),
            TestContext.Current.CancellationToken);

        Assert.Equal(2, saved.CurrentTranscriptRevision!.RevisionNumber);
        Assert.Equal("Workspace edited text.", saved.TranscriptSegments[0].Text);
    }

    [Fact]
    public async Task TranslationWorkflow_GenerateTranslationAsync_returns_refreshed_state()
    {
        var (scope, _) = await CreateWorkspaceProjectAsync();

        await scope.Workspace.Translation.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Workspace.Translation.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(translated.CurrentTranslationRevision);
        Assert.Equal("es", translated.SelectedTranslationTargetLanguage);
        Assert.Equal(2, translated.TranslatedSegments.Count);
    }

    [Fact]
    public async Task SpeakerWorkflow_RenameSpeakerAsync_returns_refreshed_state()
    {
        var (scope, created) = await CreateWorkspaceProjectAsync();

        ProjectSpeaker speaker = created.Speakers[0];
        TranscriptProjectState renamed = await scope.Workspace.Speakers.RenameSpeakerAsync(
            new RenameSpeakerRequest(speaker.Id, "Host"),
            TestContext.Current.CancellationToken);

        Assert.Equal("Host", Assert.Single(renamed.Speakers, candidate => candidate.Id == speaker.Id).DisplayName);
    }

    [Fact]
    public async Task VoiceWorkflow_AssignVoiceToSpeakerAsync_returns_refreshed_state()
    {
        var (scope, _) = await CreateWorkspaceProjectAsync();
        await scope.Workspace.Translation.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Workspace.Translation.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState assigned = await scope.Workspace.Voices.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        Assert.Contains(assigned.VoiceAssignments, assignment =>
            assignment.SpeakerId == translated.Speakers[0].Id &&
            assignment.VoiceVariant == "af_heart");
    }

    [Fact]
    public async Task TtsWorkflow_GenerateTtsForSpeakerAsync_returns_refreshed_state()
    {
        var (scope, _) = await CreateWorkspaceProjectAsync(ttsEngine: new FakeTtsEngine { DurationSamples = 168000 });
        await scope.Workspace.Translation.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Workspace.Translation.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Workspace.Voices.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        TranscriptProjectState tts = await scope.Workspace.Tts.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        Assert.Single(tts.TtsTakes);
        Assert.Contains(tts.ProjectState.Artifacts, artifact => artifact.Kind == ArtifactKind.TtsTake);
    }

    [Fact]
    public async Task TtsWorkflow_GenerateTtsForSpeakerAsync_rejects_overlapping_runs()
    {
        var ttsEngine = new BlockingTtsEngine();
        var (scope, _) = await CreateWorkspaceProjectAsync(ttsEngine: ttsEngine);
        await scope.Workspace.Translation.SetTranscriptLanguageAsync(new SetTranscriptLanguageRequest("en"), TestContext.Current.CancellationToken);
        TranscriptProjectState translated = await scope.Workspace.Translation.GenerateTranslationAsync(
            new GenerateTranslationRequest("en", "es"),
            TestContext.Current.CancellationToken);
        await scope.Workspace.Voices.AssignVoiceToSpeakerAsync(
            new AssignVoiceToSpeakerRequest(translated.Speakers[0].Id, "af_heart"),
            TestContext.Current.CancellationToken);

        Task<TranscriptProjectState> firstRun = scope.Workspace.Tts.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);
        await ttsEngine.WaitForSynthesisStartedAsync();

        Task<TranscriptProjectState> secondRun = scope.Workspace.Tts.GenerateTtsForSpeakerAsync(
            new GenerateTtsForSpeakerRequest(translated.Speakers[0].Id),
            TestContext.Current.CancellationToken);

        try
        {
            Task completed = await Task.WhenAny(secondRun, Task.Delay(TimeSpan.FromMilliseconds(250), TestContext.Current.CancellationToken));
            Assert.Same(secondRun, completed);
            InvalidOperationException exception = await Assert.ThrowsAsync<InvalidOperationException>(() => secondRun);
            Assert.Contains("already running", exception.Message);
        }
        finally
        {
            ttsEngine.Release();
            await firstRun;
            try
            {
                await secondRun;
            }
            catch (InvalidOperationException)
            {
            }
        }
    }

    [Fact]
    public async Task EditingHistoryWorkflow_RestoreEditingStateAsync_returns_refreshed_state()
    {
        var (scope, created) = await CreateWorkspaceProjectAsync(enableSpeakerDiarization: false);
        TranscriptProjectState split = await scope.Workspace.Transcript.SplitSegmentAsync(
            new SplitTranscriptSegmentRequest(created.CurrentTranscriptRevision!.Id, created.TranscriptSegments[0].Id, 2.9),
            TestContext.Current.CancellationToken);

        TranscriptProjectState restored = await scope.Workspace.EditingHistory.RestoreEditingStateAsync(
            new RestoreEditingStateRequest(
                created.SelectedTranslationTargetLanguage,
                created.TranscriptSegments,
                created.CurrentTranslationRevision is null ? null : created.TranslatedSegments,
                created.Speakers.ToDictionary(speaker => speaker.Id, speaker => speaker.DisplayName),
                created.VoiceAssignments),
            TestContext.Current.CancellationToken);

        Assert.Equal(3, split.TranscriptSegments.Count);
        Assert.Equal(2, restored.TranscriptSegments.Count);
        Assert.Equal(3, restored.CurrentTranscriptRevision!.RevisionNumber);
    }

    [Fact]
    public void DiarizationModelWorkflow_reports_required_model_status()
    {
        string tempDirectory = CreateTempDirectory();
        string modelCacheRoot = Path.Combine(tempDirectory, "model-cache");
        var handler = new DiarizationStageHandler(
            new RecordingDiarizationEngine(),
            new RecordingModelDownloader(),
            modelCacheRoot: modelCacheRoot,
            expectedSha256: SortFormerTestFixtures.ExpectedSha256);
        FakeServiceScope scope = CreateScope(
            tempDirectory,
            diarizationStageHandler: handler);

        RequiredDiarizationModelStatus? status = scope.Workspace.DiarizationModels.GetRequiredDiarizationModelStatus();

        Assert.NotNull(status);
        Assert.False(status.IsAvailable);
        Assert.True(status.CanAutoDownload);
    }
}
