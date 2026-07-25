using Trackdub.Domain.Translation;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class SqliteGlobalGlossaryRepositoryTests
{
    [Fact]
    public async Task Save_and_get_round_trip_global_entries_for_language_pair()
    {
        string root = CreateTempRoot();
        try
        {
            var database = new SqliteUserGlossaryDatabase(root);
            var repository = new SqliteGlobalGlossaryRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            GlossaryEntry entry = GlossaryEntry.CreateGlobal("en", "es", "Server", "servidor", false, now);

            await repository.SaveAsync(entry, TestContext.Current.CancellationToken);
            IReadOnlyList<GlossaryEntry> entries = await repository.GetEntriesAsync("en", "es", TestContext.Current.CancellationToken);

            GlossaryEntry loaded = Assert.Single(entries);
            Assert.Equal(entry.Id, loaded.Id);
            Assert.Equal(GlossaryScopeIds.Global, loaded.ProjectId);
            Assert.Equal("Server", loaded.SourceTerm);
            Assert.Equal("servidor", loaded.TargetTerm);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task DeleteAsync_removes_entry_by_id()
    {
        string root = CreateTempRoot();
        try
        {
            var database = new SqliteUserGlossaryDatabase(root);
            var repository = new SqliteGlobalGlossaryRepository(database);
            GlossaryEntry entry = GlossaryEntry.CreateGlobal("en", "de", "Server", "Server", false, DateTimeOffset.UtcNow);

            await repository.SaveAsync(entry, TestContext.Current.CancellationToken);
            await repository.DeleteAsync(entry.Id, TestContext.Current.CancellationToken);
            IReadOnlyList<GlossaryEntry> entries = await repository.GetEntriesAsync("en", "de", TestContext.Current.CancellationToken);

            Assert.Empty(entries);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    private static string CreateTempRoot()
    {
        string root = Path.Combine(Path.GetTempPath(), "trackdub-glossary-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }
}
