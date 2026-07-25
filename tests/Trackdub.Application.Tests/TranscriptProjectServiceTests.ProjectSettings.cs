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
    public async Task OpenAsync_when_manifest_has_no_transcript_language_returns_unknown_language_state()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        scope.ArtifactStore.Remove(ProjectArtifactPaths.ManifestRelativePath);
        TranscriptProjectState reopened = await scope.Service.OpenAsync(TestContext.Current.CancellationToken);

        Assert.Null(reopened.TranscriptLanguage);
        Assert.Null(reopened.ProjectUiSettings);
    }

    [Fact]
    public async Task OpenAsync_rechecks_export_tool_availability()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var exportTools = new MutableExportToolAvailabilityService(
            ExportToolAvailability.Unavailable("ffmpeg missing"));

        FakeServiceScope scope = CreateScope(tempDirectory, exportToolAvailabilityService: exportTools);
        TranscriptProjectState created = await scope.Service.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        Assert.NotNull(created.ExportTools);
        Assert.False(created.ExportTools!.IsAvailable);

        exportTools.Availability = ExportToolAvailability.Available("ffmpeg", "ffprobe");
        TranscriptProjectState reopened = await scope.Service.OpenAsync(TestContext.Current.CancellationToken);

        Assert.True(reopened.ExportTools?.IsAvailable);
        Assert.True(exportTools.CheckCount >= 2);
    }

    [Fact]
    public async Task ProjectWorkflow_SaveUiSettingsAsync_persists_mix_export_settings_and_restores_on_reopen()
    {
        string tempDirectory = CreateTempDirectory();
        string sourcePath = Path.Combine(tempDirectory, "sample.mp4");
        await File.WriteAllBytesAsync(sourcePath, [1, 2, 3, 4], TestContext.Current.CancellationToken);
        var expected = new ProjectUiSettings(
            Mix: new ProjectMixSettings(
                SourceGainDb: -4.5d,
                DubbedSpeechGainDb: 2.25d,
                DuckingGainDb: -10.5d,
                DuckingGainExplicit: true,
                RestoreOriginalPan: true),
            Export: new ProjectExportSettings(
                SubtitleFormats: [ExportSubtitleFormat.Srt, ExportSubtitleFormat.Ass],
                SubtitleSource: ExportSubtitleSource.Bilingual,
                BurnInSubtitles: true,
                TargetLufs: -23d,
                Container: ExportOutputContainer.Mkv,
                MatchOriginalLoudness: true));

        FakeServiceScope scope = CreateScope(tempDirectory);
        await scope.Workspace.Project.CreateAsync(
            new CreateTranscriptProjectRequest("Transcript Demo", sourcePath),
            TestContext.Current.CancellationToken);

        TranscriptProjectState saved = await scope.Workspace.Project.SaveUiSettingsAsync(
            expected,
            TestContext.Current.CancellationToken);
        ProjectManifest? manifest = await scope.ArtifactStore.ReadJsonAsync<ProjectManifest>(
            ProjectArtifactPaths.ManifestRelativePath,
            TestContext.Current.CancellationToken);
        TranscriptProjectState reopenedSameSession = await scope.Workspace.Project.OpenAsync(TestContext.Current.CancellationToken);
        TranscriptProjectState reopenedService = await scope.CreateReopenedStateService().OpenAsync(null, TestContext.Current.CancellationToken);

        Assert.NotNull(manifest);
        AssertProjectUiSettings(expected, saved.ProjectUiSettings);
        AssertProjectUiSettings(expected, manifest!.UiSettings);
        AssertProjectUiSettings(expected, reopenedSameSession.ProjectUiSettings);
        AssertProjectUiSettings(expected, reopenedService.ProjectUiSettings);
    }
}
