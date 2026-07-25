using Trackdub.Domain.Artifacts;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteMediaAssetRepositoryTests
{
    [Fact]
    public async Task Repository_round_trips_media_asset_and_artifacts_for_reopen()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Reopen.trackdub");
        var database = new SqliteProjectDatabase(projectRoot);
        var projectRepository = new SqliteProjectRepository(database);
        var mediaRepository = new SqliteMediaAssetRepository(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Reopen", now, now);
        var mediaAsset = new MediaAsset(Guid.NewGuid(), project.Id, @"D:\media\source.mp4", "source.mp4", "abc", 123, now, "mp4", 1.2, true, true, now);
        Guid stageRunId = Guid.NewGuid();
        var audioArtifact = new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.NormalizedAudio, "media/normalized_audio.wav", "def", 456, 1.1, 48000, 1, now, stageRunId, "generated-audio");
        var waveformArtifact = new ProjectArtifact(Guid.NewGuid(), project.Id, mediaAsset.Id, ArtifactKind.WaveformSummary, "artifacts/waveform/normalized_audio.waveform.json", "ghi", 789, 1.1, 48000, 1, now);

        try
        {
            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            await mediaRepository.SaveAsync(mediaAsset, TestContext.Current.CancellationToken);
            await mediaRepository.SaveArtifactAsync(audioArtifact, TestContext.Current.CancellationToken);
            await mediaRepository.SaveArtifactAsync(waveformArtifact, TestContext.Current.CancellationToken);

            TrackdubProject? reopenedProject = await projectRepository.GetAsync(TestContext.Current.CancellationToken);
            MediaAsset? reopenedAsset = await mediaRepository.GetPrimaryAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<ProjectArtifact> reopenedArtifacts = await mediaRepository.GetArtifactsAsync(project.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(reopenedProject);
            Assert.NotNull(reopenedAsset);
            Assert.Equal(project.Name, reopenedProject!.Name);
            Assert.Equal(mediaAsset.SourceFilePath, reopenedAsset!.SourceFilePath);
            Assert.Equal(mediaAsset.SourceFileName, reopenedAsset!.SourceFileName);
            Assert.Equal(2, reopenedArtifacts.Count);
            Assert.Contains(reopenedArtifacts, artifact => artifact.Kind == ArtifactKind.NormalizedAudio);
            Assert.Contains(reopenedArtifacts, artifact => artifact.Kind == ArtifactKind.WaveformSummary);
            ProjectArtifact reopenedAudio = Assert.Single(reopenedArtifacts, artifact => artifact.Kind == ArtifactKind.NormalizedAudio);
            Assert.Equal(stageRunId, reopenedAudio.StageRunId);
            Assert.Equal("generated-audio", reopenedAudio.Provenance);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }

    [Fact]
    public async Task UpdateSourcePathAsync_persists_relocated_path()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Relocate.trackdub");
        var database = new SqliteProjectDatabase(projectRoot);
        var projectRepository = new SqliteProjectRepository(database);
        var mediaRepository = new SqliteMediaAssetRepository(database);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Relocate", now, now);
        var mediaAsset = new MediaAsset(Guid.NewGuid(), project.Id, @"D:\media\source.mp4", "source.mp4", "abc", 123, now, "mp4", 1.2, true, true, now);

        try
        {
            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            await mediaRepository.SaveAsync(mediaAsset, TestContext.Current.CancellationToken);
            await mediaRepository.UpdateSourcePathAsync(mediaAsset.Id, @"D:\media\moved\source.mp4", "source.mp4", TestContext.Current.CancellationToken);

            MediaAsset? reopenedAsset = await mediaRepository.GetPrimaryAsync(project.Id, TestContext.Current.CancellationToken);

            Assert.NotNull(reopenedAsset);
            Assert.Equal(@"D:\media\moved\source.mp4", reopenedAsset!.SourceFilePath);
        }
        finally
        {
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
