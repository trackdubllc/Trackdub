using Trackdub.Domain.Projects;
using Trackdub.Domain.Translation;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteGlossaryRepositoryTests
{
    [Fact]
    public async Task Repository_round_trips_filters_and_deletes_project_glossary_entries()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), "Glossary.trackdub");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var glossaryRepository = new SqliteGlossaryRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Glossary", now, now);
            var otherProject = new TrackdubProject(Guid.NewGuid(), "Other", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);
            await projectRepository.InitializeAsync(otherProject, TestContext.Current.CancellationToken);

            GlossaryEntry spanishEntry = GlossaryEntry.Create(project.Id, "EN", "ES", "Warp Core", "nucleo warp", false, now);
            GlossaryEntry germanEntry = GlossaryEntry.Create(project.Id, "en", "de", "Warp Core", "Warpkern", false, now);
            GlossaryEntry otherProjectEntry = GlossaryEntry.Create(otherProject.Id, "en", "es", "Warp Core", "nucleo warp", false, now);

            await glossaryRepository.SaveAsync(spanishEntry, TestContext.Current.CancellationToken);
            await glossaryRepository.SaveAsync(germanEntry, TestContext.Current.CancellationToken);
            await glossaryRepository.SaveAsync(otherProjectEntry, TestContext.Current.CancellationToken);
            GlossaryEntry updatedSpanishEntry = spanishEntry with
            {
                TargetTerm = "motor warp",
                CreatedAtUtc = now.AddDays(1),
                UpdatedAtUtc = now.AddDays(2)
            };
            await glossaryRepository.SaveAsync(updatedSpanishEntry, TestContext.Current.CancellationToken);

            IReadOnlyList<GlossaryEntry> spanishEntries = await glossaryRepository.GetEntriesAsync(project.Id, " en ", " es ", TestContext.Current.CancellationToken);
            await glossaryRepository.DeleteAsync(otherProject.Id, spanishEntry.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<GlossaryEntry> afterWrongProjectDelete = await glossaryRepository.GetEntriesAsync(project.Id, "en", "es", TestContext.Current.CancellationToken);
            await glossaryRepository.DeleteAsync(project.Id, spanishEntry.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<GlossaryEntry> afterDelete = await glossaryRepository.GetEntriesAsync(project.Id, "en", "es", TestContext.Current.CancellationToken);

            GlossaryEntry reloaded = Assert.Single(spanishEntries);
            Assert.Equal(spanishEntry.Id, reloaded.Id);
            Assert.Equal(project.Id, reloaded.ProjectId);
            Assert.Equal("en", reloaded.SourceLanguage);
            Assert.Equal("es", reloaded.TargetLanguage);
            Assert.Equal("Warp Core", reloaded.SourceTerm);
            Assert.Equal("motor warp", reloaded.TargetTerm);
            Assert.False(reloaded.IsCaseSensitive);
            Assert.Equal(spanishEntry.CreatedAtUtc, reloaded.CreatedAtUtc);
            Assert.Equal(updatedSpanishEntry.UpdatedAtUtc, reloaded.UpdatedAtUtc);
            Assert.Equal(spanishEntry.Id, Assert.Single(afterWrongProjectDelete).Id);
            Assert.Empty(afterDelete);
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
