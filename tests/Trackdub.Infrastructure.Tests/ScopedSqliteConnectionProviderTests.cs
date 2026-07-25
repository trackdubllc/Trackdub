using System.Data;
using System.Data.Common;
using Trackdub.Domain;
using Trackdub.Domain.Media;
using Trackdub.Domain.Projects;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class ScopedSqliteConnectionProviderTests
{
    [Fact]
    public async Task Project_repositories_share_scoped_connection_without_disposing_it()
    {
        string projectRoot = Path.Combine(
            Path.GetTempPath(),
            "Trackdub.Infrastructure.Tests",
            Guid.NewGuid().ToString("N"),
            "ScopedConnection.trackdub");
        var database = new SqliteProjectDatabase(projectRoot);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        var project = new TrackdubProject(Guid.NewGuid(), "Scoped Connection", now, now);
        var mediaAsset = new MediaAsset(
            Guid.NewGuid(),
            project.Id,
            @"D:\media\source.mp4",
            "source.mp4",
            "abc",
            123,
            now,
            "mp4",
            1.2d,
            HasAudio: true,
            HasVideo: true,
            now);
        StageRunRecord stageRun = StageRunRecord.Start(project.Id, "asr", now);

        try
        {
            await database.InitializeAsync(TestContext.Current.CancellationToken);
            using var provider = new ScopedSqliteConnectionProvider(database.DatabasePath);
            var projectRepository = new SqliteProjectRepository(database, provider);
            var mediaRepository = new SqliteMediaAssetRepository(database, provider);
            var stageRunStore = new SqliteProjectStageRunStore(database, provider);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            await mediaRepository.SaveAsync(mediaAsset, TestContext.Current.CancellationToken);
            await stageRunStore.CreateAsync(stageRun, TestContext.Current.CancellationToken);

            Assert.Equal(ConnectionState.Open, provider.Connection.State);

            TrackdubProject? reopenedProject = await projectRepository.GetAsync(TestContext.Current.CancellationToken);
            MediaAsset? reopenedMediaAsset = await mediaRepository.GetPrimaryAsync(project.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<StageRunRecord> reopenedStageRuns = await stageRunStore.ListByProjectAsync(project.Id, TestContext.Current.CancellationToken);

            Assert.Equal(ConnectionState.Open, provider.Connection.State);
            Assert.NotNull(reopenedProject);
            Assert.NotNull(reopenedMediaAsset);
            Assert.Single(reopenedStageRuns);

            using DbCommand command = provider.Connection.CreateCommand();
            command.CommandText = "SELECT COUNT(*) FROM projects;";
            object? result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
            Assert.Equal(1L, Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture));
        }
        finally
        {
            SqliteConnection.ClearAllPools();
            if (Directory.Exists(projectRoot))
            {
                Directory.Delete(projectRoot, recursive: true);
            }
        }
    }
}
