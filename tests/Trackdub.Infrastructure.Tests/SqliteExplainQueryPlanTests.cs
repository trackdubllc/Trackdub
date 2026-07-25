using Microsoft.Data.Sqlite;
using Trackdub.Domain;
using Trackdub.Domain.Projects;
using Trackdub.Domain.Translation;
using Trackdub.Domain.Transcript;
using Trackdub.Infrastructure.Persistence.Sqlite;

namespace Trackdub.Infrastructure.Tests;

/// <summary>
/// M20 SQLite hot-path audit: ensure representative queries plan as indexed SEARCH, not full SCAN.
/// Expected indexes (see <c>SqliteProjectSchemaMigrations</c>):
/// <list type="bullet">
/// <item><description><c>glossary_entries</c> → <c>ix_glossary_entries_project_language</c></description></item>
/// <item><description><c>StageRuns</c> → <c>ix_stage_runs_project_id</c></description></item>
/// <item><description><c>transcript_segments</c> → <c>ix_transcript_segments_revision_id</c></description></item>
/// </list>
/// </summary>
public sealed class SqliteExplainQueryPlanTests
{
    private const int LargeGlossaryEntryCount = 400;
    private const int LargeStageRunCount = 250;
    private const int LargeTranscriptSegmentCount = 1_200;

    private const string GlossaryLanguagePairIndex = "ix_glossary_entries_project_language";
    private const string StageRunsProjectIndex = "ix_stage_runs_project_id";
    private const string TranscriptSegmentsRevisionIndex = "ix_transcript_segments_revision_id";

    [Fact]
    public async Task Project_schema_includes_explain_audit_indexes()
    {
        string projectRoot = CreateTempProjectRoot("ExplainSchema");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            await database.InitializeAsync(TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);

            Assert.True(
                await IndexExistsAsync(connection, StageRunsProjectIndex, TestContext.Current.CancellationToken),
                $"Missing index {StageRunsProjectIndex} after schema migration.");
            Assert.True(
                await IndexExistsAsync(connection, TranscriptSegmentsRevisionIndex, TestContext.Current.CancellationToken),
                $"Missing index {TranscriptSegmentsRevisionIndex} after schema migration.");

            if (await TableExistsAsync(connection, "glossary_entries", TestContext.Current.CancellationToken))
            {
                Assert.True(
                    await IndexExistsAsync(connection, GlossaryLanguagePairIndex, TestContext.Current.CancellationToken),
                    $"Missing index {GlossaryLanguagePairIndex} after schema migration.");
            }
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Hot path: glossary by project + language pair. Expects <see cref="GlossaryLanguagePairIndex"/>.</summary>
    [Fact]
    public async Task Glossary_entries_by_language_pair_uses_indexed_search_when_table_exists()
    {
        string projectRoot = CreateTempProjectRoot("ExplainGlossary");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            await database.InitializeAsync(TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            if (!await TableExistsAsync(connection, "glossary_entries", TestContext.Current.CancellationToken)
                || !await IndexExistsAsync(connection, GlossaryLanguagePairIndex, TestContext.Current.CancellationToken))
            {
                return;
            }

            var projectRepository = new SqliteProjectRepository(database);
            var glossaryRepository = new SqliteGlossaryRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Explain Glossary", now, now);
            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            for (int i = 0; i < 8; i++)
            {
                GlossaryEntry entry = GlossaryEntry.Create(project.Id, "en", "es", $"term-{i}", $"termino-{i}", false, now);
                await glossaryRepository.SaveAsync(entry, TestContext.Current.CancellationToken);
            }

            string plan = await ExplainAsync(
                connection,
                """
                SELECT id
                FROM glossary_entries
                WHERE project_id = $projectId
                  AND source_language = $sourceLanguage
                  AND target_language = $targetLanguage
                ORDER BY source_term, target_term;
                """,
                TestContext.Current.CancellationToken,
                ("$projectId", project.Id.ToString("D")),
                ("$sourceLanguage", "en"),
                ("$targetLanguage", "es"));

            AssertUsesIndexedSearch(plan, "glossary_entries");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Hot path: stage runs by project. Expects <see cref="StageRunsProjectIndex"/>.</summary>
    [Fact]
    public async Task Stage_runs_by_project_uses_indexed_search()
    {
        string projectRoot = CreateTempProjectRoot("ExplainStageRuns");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Explain StageRuns", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            for (int i = 0; i < 6; i++)
            {
                StageRunRecord stageRun = StageRunRecord.Start(project.Id, $"stage-{i}", now.AddSeconds(i));
                await stageRunStore.CreateAsync(stageRun, TestContext.Current.CancellationToken);
            }

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            string plan = await ExplainAsync(
                connection,
                """
                SELECT Id
                FROM StageRuns
                WHERE ProjectId = $projectId
                ORDER BY StartedAtUtc, Id;
                """,
                TestContext.Current.CancellationToken,
                ("$projectId", project.Id.ToString("D")));

            AssertUsesIndexedSearch(plan, "StageRuns");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Hot path: transcript segments by revision. Expects <see cref="TranscriptSegmentsRevisionIndex"/>.</summary>
    [Fact]
    public async Task Transcript_segments_by_revision_uses_indexed_search()
    {
        string projectRoot = CreateTempProjectRoot("ExplainSegments");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Explain Segments", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            StageRunRecord stageRun = StageRunRecord.Start(project.Id, "asr", now);
            await stageRunStore.CreateAsync(stageRun, TestContext.Current.CancellationToken);

            TranscriptRevision revision = TranscriptRevision.Create(project.Id, stageRun.Id, revisionNumber: 1, now);
            TranscriptSegment[] segments =
            [
                TranscriptSegment.Create(revision.Id, 0, 0.0, 1.0, "one"),
                TranscriptSegment.Create(revision.Id, 1, 1.0, 2.0, "two"),
                TranscriptSegment.Create(revision.Id, 2, 2.0, 3.0, "three"),
                TranscriptSegment.Create(revision.Id, 3, 3.0, 4.0, "four"),
            ];
            await transcriptRepository.SaveRevisionAsync(revision, segments, TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            string plan = await ExplainAsync(
                connection,
                """
                SELECT id
                FROM transcript_segments
                WHERE transcript_revision_id = $transcriptRevisionId
                ORDER BY segment_index;
                """,
                TestContext.Current.CancellationToken,
                ("$transcriptRevisionId", revision.Id.ToString("D")));

            AssertUsesIndexedSearch(plan, "transcript_segments");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Empty glossary project (0 rows). Expects <see cref="GlossaryLanguagePairIndex"/> SEARCH plan.</summary>
    [Fact]
    public async Task Glossary_empty_project_explain_succeeds_with_no_matching_rows()
    {
        string projectRoot = CreateTempProjectRoot("ExplainGlossaryEmpty");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            await database.InitializeAsync(TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            if (!await TableExistsAsync(connection, "glossary_entries", TestContext.Current.CancellationToken)
                || !await IndexExistsAsync(connection, GlossaryLanguagePairIndex, TestContext.Current.CancellationToken))
            {
                return;
            }

            var projectRepository = new SqliteProjectRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Empty Glossary", now, now);
            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            string plan = await ExplainAsync(
                connection,
                """
                SELECT id
                FROM glossary_entries
                WHERE project_id = $projectId
                  AND source_language = $sourceLanguage
                  AND target_language = $targetLanguage
                ORDER BY source_term, target_term;
                """,
                TestContext.Current.CancellationToken,
                ("$projectId", project.Id.ToString("D")),
                ("$sourceLanguage", "en"),
                ("$targetLanguage", "es"));

            AssertExplainProducesPlan(plan, "glossary_entries");
            AssertUsesIndexedSearch(plan, "glossary_entries");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Empty stage-run project (0 rows). Expects <see cref="StageRunsProjectIndex"/> SEARCH plan.</summary>
    [Fact]
    public async Task Stage_runs_empty_project_uses_indexed_search()
    {
        string projectRoot = CreateTempProjectRoot("ExplainStageRunsEmpty");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Empty StageRuns", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            string plan = await ExplainAsync(
                connection,
                """
                SELECT Id
                FROM StageRuns
                WHERE ProjectId = $projectId
                ORDER BY StartedAtUtc, Id;
                """,
                TestContext.Current.CancellationToken,
                ("$projectId", project.Id.ToString("D")));

            AssertExplainProducesPlan(plan, "StageRuns");
            AssertUsesIndexedSearch(plan, "StageRuns");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Empty transcript revision (0 segments). Expects <see cref="TranscriptSegmentsRevisionIndex"/> SEARCH plan.</summary>
    [Fact]
    public async Task Transcript_segments_empty_revision_uses_indexed_search()
    {
        string projectRoot = CreateTempProjectRoot("ExplainSegmentsEmpty");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Empty Segments", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            StageRunRecord stageRun = StageRunRecord.Start(project.Id, "asr", now);
            await stageRunStore.CreateAsync(stageRun, TestContext.Current.CancellationToken);

            TranscriptRevision revision = TranscriptRevision.Create(project.Id, stageRun.Id, revisionNumber: 1, now);
            await transcriptRepository.SaveRevisionAsync(revision, Array.Empty<TranscriptSegment>(), TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            string plan = await ExplainAsync(
                connection,
                """
                SELECT id
                FROM transcript_segments
                WHERE transcript_revision_id = $transcriptRevisionId
                ORDER BY segment_index;
                """,
                TestContext.Current.CancellationToken,
                ("$transcriptRevisionId", revision.Id.ToString("D")));

            AssertExplainProducesPlan(plan, "transcript_segments");
            AssertUsesIndexedSearch(plan, "transcript_segments");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Large glossary fixture (~400 rows). Expects <see cref="GlossaryLanguagePairIndex"/> SEARCH plan.</summary>
    [Fact]
    public async Task Glossary_large_language_pair_uses_indexed_search_when_table_exists()
    {
        string projectRoot = CreateTempProjectRoot("ExplainGlossaryLarge");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            await database.InitializeAsync(TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            if (!await TableExistsAsync(connection, "glossary_entries", TestContext.Current.CancellationToken)
                || !await IndexExistsAsync(connection, GlossaryLanguagePairIndex, TestContext.Current.CancellationToken))
            {
                return;
            }

            var projectRepository = new SqliteProjectRepository(database);
            var glossaryRepository = new SqliteGlossaryRepository(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Large Glossary", now, now);
            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            for (int i = 0; i < LargeGlossaryEntryCount; i++)
            {
                GlossaryEntry entry = GlossaryEntry.Create(project.Id, "en", "es", $"term-{i:D4}", $"termino-{i:D4}", false, now);
                await glossaryRepository.SaveAsync(entry, TestContext.Current.CancellationToken);
            }

            string plan = await ExplainAsync(
                connection,
                """
                SELECT id
                FROM glossary_entries
                WHERE project_id = $projectId
                  AND source_language = $sourceLanguage
                  AND target_language = $targetLanguage
                ORDER BY source_term, target_term;
                """,
                TestContext.Current.CancellationToken,
                ("$projectId", project.Id.ToString("D")),
                ("$sourceLanguage", "en"),
                ("$targetLanguage", "es"));

            AssertUsesIndexedSearch(plan, "glossary_entries");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Large stage-run fixture (~250 rows). Expects <see cref="StageRunsProjectIndex"/> SEARCH plan.</summary>
    [Fact]
    public async Task Stage_runs_large_project_uses_indexed_search()
    {
        string projectRoot = CreateTempProjectRoot("ExplainStageRunsLarge");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Large StageRuns", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            for (int i = 0; i < LargeStageRunCount; i++)
            {
                StageRunRecord stageRun = StageRunRecord.Start(project.Id, $"stage-{i:D3}", now.AddSeconds(i));
                await stageRunStore.CreateAsync(stageRun, TestContext.Current.CancellationToken);
            }

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            string plan = await ExplainAsync(
                connection,
                """
                SELECT Id
                FROM StageRuns
                WHERE ProjectId = $projectId
                ORDER BY StartedAtUtc, Id;
                """,
                TestContext.Current.CancellationToken,
                ("$projectId", project.Id.ToString("D")));

            AssertUsesIndexedSearch(plan, "StageRuns");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    /// <summary>Large transcript fixture (~1.2k segments). Expects <see cref="TranscriptSegmentsRevisionIndex"/> SEARCH plan.</summary>
    [Fact]
    public async Task Transcript_segments_large_revision_uses_indexed_search()
    {
        string projectRoot = CreateTempProjectRoot("ExplainSegmentsLarge");
        try
        {
            var database = new SqliteProjectDatabase(projectRoot);
            var projectRepository = new SqliteProjectRepository(database);
            var transcriptRepository = new SqliteTranscriptRepository(database);
            var stageRunStore = new SqliteProjectStageRunStore(database);
            DateTimeOffset now = DateTimeOffset.UtcNow;
            var project = new TrackdubProject(Guid.NewGuid(), "Large Segments", now, now);

            await projectRepository.InitializeAsync(project, TestContext.Current.CancellationToken);

            StageRunRecord stageRun = StageRunRecord.Start(project.Id, "asr", now);
            await stageRunStore.CreateAsync(stageRun, TestContext.Current.CancellationToken);

            TranscriptRevision revision = TranscriptRevision.Create(project.Id, stageRun.Id, revisionNumber: 1, now);
            TranscriptSegment[] segments = new TranscriptSegment[LargeTranscriptSegmentCount];
            for (int i = 0; i < LargeTranscriptSegmentCount; i++)
            {
                double start = i;
                segments[i] = TranscriptSegment.Create(revision.Id, i, start, start + 1.0, $"segment-{i:D4}");
            }

            await transcriptRepository.SaveRevisionAsync(revision, segments, TestContext.Current.CancellationToken);

            await using SqliteConnection connection = await OpenConnectionAsync(database, TestContext.Current.CancellationToken);
            string plan = await ExplainAsync(
                connection,
                """
                SELECT id
                FROM transcript_segments
                WHERE transcript_revision_id = $transcriptRevisionId
                ORDER BY segment_index;
                """,
                TestContext.Current.CancellationToken,
                ("$transcriptRevisionId", revision.Id.ToString("D")));

            AssertUsesIndexedSearch(plan, "transcript_segments");
        }
        finally
        {
            CleanupProjectRoot(projectRoot);
        }
    }

    private static string CreateTempProjectRoot(string label) =>
        Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"), $"{label}.trackdub");

    private static void CleanupProjectRoot(string projectRoot)
    {
        SqliteConnection.ClearAllPools();
        if (Directory.Exists(projectRoot))
        {
            Directory.Delete(projectRoot, recursive: true);
        }
    }

    private static async Task<SqliteConnection> OpenConnectionAsync(SqliteProjectDatabase database, CancellationToken cancellationToken)
    {
        var connection = new SqliteConnection($"Data Source={database.DatabasePath}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        return connection;
    }

    private static async Task<bool> IndexExistsAsync(SqliteConnection connection, string indexName, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'index' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", indexName);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<bool> TableExistsAsync(SqliteConnection connection, string tableName, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name LIMIT 1;";
        command.Parameters.AddWithValue("$name", tableName);
        object? result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is not null;
    }

    private static async Task<string> ExplainAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object? Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"EXPLAIN QUERY PLAN {sql}";
        foreach ((string name, object? value) in parameters)
        {
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        }

        var lines = new List<string>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            lines.Add(reader.GetString(3));
        }

        return string.Join('\n', lines);
    }

    private static void AssertExplainProducesPlan(string plan, string tableName)
    {
        Assert.False(string.IsNullOrWhiteSpace(plan), $"EXPLAIN QUERY PLAN returned no detail rows for {tableName}.");
    }

    private static void AssertUsesIndexedSearch(string plan, string tableName)
    {
        Assert.False(string.IsNullOrWhiteSpace(plan), "EXPLAIN QUERY PLAN returned no detail rows.");

        bool usesSearch = plan.Contains("SEARCH", StringComparison.OrdinalIgnoreCase);
        Assert.True(usesSearch, $"Expected SEARCH in plan for {tableName}. Plan:{Environment.NewLine}{plan}");

        bool usesTableScan = plan.Contains($"SCAN {tableName}", StringComparison.OrdinalIgnoreCase);
        Assert.False(usesTableScan, $"Expected indexed access for {tableName}, got SCAN. Plan:{Environment.NewLine}{plan}");
    }
}
