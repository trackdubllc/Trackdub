using System.Text;
using Trackdub.Application.Transcripts;
using Trackdub.Domain.Translation;
using Trackdub.TestDoubles;

namespace Trackdub.Application.Tests;

public sealed class GlossaryServiceTests
{
    [Fact]
    public async Task ImportCsvAsync_creates_entries_for_language_pair()
    {
        var repository = new FakeGlossaryRepository();
        var service = new GlossaryService(repository);
        Guid projectId = Guid.NewGuid();
        await using Stream csv = CreateCsv(
            """
            source,target
            warp core,nucleo warp
            Mira,Mira
            """);

        IReadOnlyList<GlossaryEntry> imported = await service.ImportCsvAsync(
            projectId,
            " EN ",
            " ES ",
            csv,
            isCaseSensitive: false,
            TestContext.Current.CancellationToken);

        IReadOnlyList<GlossaryEntry> entries = await repository.GetEntriesAsync(projectId, "en", "es", TestContext.Current.CancellationToken);
        Assert.Equal(2, imported.Count);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal(projectId, entry.ProjectId);
            Assert.Equal("en", entry.SourceLanguage);
            Assert.Equal("es", entry.TargetLanguage);
            Assert.False(entry.IsCaseSensitive);
        });
        Assert.Contains(entries, entry => entry.SourceTerm == "warp core" && entry.TargetTerm == "nucleo warp");
        Assert.Contains(entries, entry => entry.SourceTerm == "Mira" && entry.TargetTerm == "Mira");
    }

    [Theory]
    [InlineData("source,target\nonly-source\n")]
    [InlineData("source,target\n,servidor\n")]
    [InlineData("source,target\nserver,\n")]
    public async Task ImportCsvAsync_rejects_malformed_or_empty_rows(string csvText)
    {
        var repository = new FakeGlossaryRepository();
        var service = new GlossaryService(repository);
        await using Stream csv = CreateCsv(csvText);

        await Assert.ThrowsAsync<InvalidDataException>(() => service.ImportCsvAsync(
            Guid.NewGuid(),
            "en",
            "es",
            csv,
            isCaseSensitive: false,
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task GetConflictsAsync_reports_duplicate_source_terms_with_different_targets()
    {
        var repository = new FakeGlossaryRepository();
        var service = new GlossaryService(repository);
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repository.SaveAsync(GlossaryEntry.Create(projectId, "en", "es", "Server", "servidor", false, now), TestContext.Current.CancellationToken);
        await repository.SaveAsync(GlossaryEntry.Create(projectId, "en", "es", " server ", "equipo", false, now), TestContext.Current.CancellationToken);
        await repository.SaveAsync(GlossaryEntry.Create(projectId, "en", "de", "Server", "Server", false, now), TestContext.Current.CancellationToken);

        IReadOnlyList<GlossaryConflict> conflicts = await service.GetConflictsAsync(projectId, "en", "es", TestContext.Current.CancellationToken);

        GlossaryConflict conflict = Assert.Single(conflicts);
        Assert.Equal("server", conflict.NormalizedSourceTerm);
        Assert.Equal(["equipo", "servidor"], conflict.TargetTerms.Order(StringComparer.Ordinal).ToArray());
    }
    [Fact]
    public async Task GetConflictsAsync_ignores_distinct_case_sensitive_source_terms()
    {
        var repository = new FakeGlossaryRepository();
        var service = new GlossaryService(repository);
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await repository.SaveAsync(GlossaryEntry.Create(projectId, "en", "es", "Apple", "Manzana", isCaseSensitive: true, now), TestContext.Current.CancellationToken);
        await repository.SaveAsync(GlossaryEntry.Create(projectId, "en", "es", "apple", "equipo", isCaseSensitive: true, now), TestContext.Current.CancellationToken);

        IReadOnlyList<GlossaryConflict> conflicts = await service.GetConflictsAsync(projectId, "en", "es", TestContext.Current.CancellationToken);

        Assert.Empty(conflicts);
    }

    [Fact]
    public async Task ImportCsvAsync_preserves_source_term_casing_when_case_sensitive()
    {
        var repository = new FakeGlossaryRepository();
        var service = new GlossaryService(repository);
        Guid projectId = Guid.NewGuid();
        const string csv = "US,United States\n";

        IReadOnlyList<GlossaryEntry> imported = await service.ImportCsvAsync(
            projectId,
            "en",
            "es",
            new MemoryStream(Encoding.UTF8.GetBytes(csv)),
            isCaseSensitive: true,
            TestContext.Current.CancellationToken);

        GlossaryEntry entry = Assert.Single(imported);
        Assert.Equal("US", entry.SourceTerm);
        Assert.True(entry.IsCaseSensitive);
    }

    [Fact]
    public async Task GetMergedEntriesAsync_project_entry_overrides_global_for_same_normalized_source()
    {
        var projectRepository = new FakeGlossaryRepository();
        var globalRepository = new FakeGlobalGlossaryRepository();
        var service = new GlossaryService(projectRepository, globalRepository);
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await globalRepository.SaveAsync(
            GlossaryEntry.CreateGlobal("en", "es", "Server", "servidor global", false, now),
            TestContext.Current.CancellationToken);
        await projectRepository.SaveAsync(
            GlossaryEntry.Create(projectId, "en", "es", " server ", "equipo", false, now),
            TestContext.Current.CancellationToken);

        IReadOnlyList<GlossaryEntry> merged = await service.GetMergedEntriesAsync(
            projectId,
            "en",
            "es",
            TestContext.Current.CancellationToken);

        GlossaryEntry entry = Assert.Single(merged);
        Assert.Equal("equipo", entry.TargetTerm);
        Assert.Equal(projectId, entry.ProjectId);
    }


    [Fact]
    public async Task GetMergedEntriesAsync_preserves_distinct_case_sensitive_source_terms()
    {
        var projectRepository = new FakeGlossaryRepository();
        var globalRepository = new FakeGlobalGlossaryRepository();
        var service = new GlossaryService(projectRepository, globalRepository);
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await globalRepository.SaveAsync(
            GlossaryEntry.CreateGlobal("en", "es", "Apple", "Manzana", isCaseSensitive: true, now),
            TestContext.Current.CancellationToken);
        await projectRepository.SaveAsync(
            GlossaryEntry.Create(projectId, "en", "es", "apple", "equipo", isCaseSensitive: true, now),
            TestContext.Current.CancellationToken);

        IReadOnlyList<GlossaryEntry> merged = await service.GetMergedEntriesAsync(
            projectId,
            "en",
            "es",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, merged.Count);
        Assert.Contains(merged, entry => entry.SourceTerm == "Apple" && entry.TargetTerm == "Manzana");
        Assert.Contains(merged, entry => entry.SourceTerm == "apple" && entry.TargetTerm == "equipo");
    }

    [Fact]
    public async Task GetMergedEntriesAsync_preserves_same_scope_conflicting_source_terms()
    {
        var projectRepository = new FakeGlossaryRepository();
        var globalRepository = new FakeGlobalGlossaryRepository();
        var service = new GlossaryService(projectRepository, globalRepository);
        Guid projectId = Guid.NewGuid();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        await projectRepository.SaveAsync(
            GlossaryEntry.Create(projectId, "en", "es", "Server", "servidor", false, now),
            TestContext.Current.CancellationToken);
        await projectRepository.SaveAsync(
            GlossaryEntry.Create(projectId, "en", "es", " server ", "equipo", false, now),
            TestContext.Current.CancellationToken);
        await globalRepository.SaveAsync(
            GlossaryEntry.CreateGlobal("en", "es", "Server", "servidor global", false, now),
            TestContext.Current.CancellationToken);

        IReadOnlyList<GlossaryEntry> merged = await service.GetMergedEntriesAsync(
            projectId,
            "en",
            "es",
            TestContext.Current.CancellationToken);

        Assert.Equal(2, merged.Count);
        Assert.All(merged, entry => Assert.Equal(projectId, entry.ProjectId));
        Assert.Contains(merged, entry => entry.TargetTerm == "servidor");
        Assert.Contains(merged, entry => entry.TargetTerm == "equipo");
    }
    [Fact]
    public async Task ImportCsvAsync_global_scope_persists_to_global_repository()
    {
        var projectRepository = new FakeGlossaryRepository();
        var globalRepository = new FakeGlobalGlossaryRepository();
        var service = new GlossaryService(projectRepository, globalRepository);
        Guid projectId = Guid.NewGuid();
        await using Stream csv = CreateCsv(
            """
            source,target
            warp core,nucleo warp
            """);

        IReadOnlyList<GlossaryEntry> imported = await service.ImportCsvAsync(
            projectId,
            "en",
            "es",
            csv,
            isCaseSensitive: false,
            GlossaryStorageScope.Global,
            TestContext.Current.CancellationToken);

        Assert.Single(imported);
        Assert.All(imported, entry => Assert.Equal(GlossaryScopeIds.Global, entry.ProjectId));
        IReadOnlyList<GlossaryEntry> globalEntries = await globalRepository.GetEntriesAsync("en", "es", TestContext.Current.CancellationToken);
        Assert.Single(globalEntries);
        Assert.Empty(projectRepository.Entries);
    }

    private static Stream CreateCsv(string text) =>
        new MemoryStream(Encoding.UTF8.GetBytes(text));
}
