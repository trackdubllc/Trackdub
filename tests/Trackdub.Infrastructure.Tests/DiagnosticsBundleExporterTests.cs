using System.IO.Compression;
using System.Text.Json;
using Trackdub.Contracts;
using Trackdub.Contracts.ApplicationContracts;
using Trackdub.Application.Projects;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Domain;
using Trackdub.Infrastructure.Diagnostics;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Tests;

public sealed class DiagnosticsBundleExporterTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    [Fact]
    public async Task ExportBundleAsync_writes_required_bundle_files_and_redacts_username_paths()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        string projectRoot = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);
        await File.WriteAllTextAsync(
            storagePaths.LogFilePath,
            $@"Log path C:\Users\{Environment.UserName}\AppData\Local\Trackdub\trackdub.log",
            TestContext.Current.CancellationToken);

        string existingModelRoot = Path.Combine(storagePaths.ModelCacheDirectory, "demo-model");
        Directory.CreateDirectory(existingModelRoot);
        await File.WriteAllTextAsync(
            Path.Combine(existingModelRoot, "weights.onnx"),
            "x",
            TestContext.Current.CancellationToken);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        await recordStore.SaveAsync(
        [
            new LocalModelCacheRecord("demo-installed", existingModelRoot, "r1", "sha", DateTimeOffset.UtcNow),
            new LocalModelCacheRecord("demo-missing", Path.Combine(storagePaths.ModelCacheDirectory, "missing-model"), "r2", "sha", DateTimeOffset.UtcNow)
        ]);

        await CreateProjectSchemaVersionAsync(projectRoot, version: 20);

        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(
            outputPath,
            ProjectRootPath: projectRoot,
            MediaPath: $@"C:\Users\{Environment.UserName}\Videos\clip.mp4",
            FailureCategory: FailureCategory.UnknownError,
            FailureExplanation: "An unexpected error occurred and the current action was stopped.",
            FailureContext: "Unhandled UI exception"));

        using var archive = ZipFile.OpenRead(outputPath);
        Assert.NotNull(archive.GetEntry("diagnostics.json"));
        Assert.NotNull(archive.GetEntry("schema-version.txt"));
        Assert.NotNull(archive.GetEntry("model-cache-summary.json"));
        Assert.NotNull(archive.GetEntry("hardware-info.json"));
        Assert.NotNull(archive.GetEntry("logs/trackdub.log"));

        string diagnosticsJson = await ReadEntryAsync(archive, "diagnostics.json");
        using (JsonDocument diagnosticsDocument = JsonDocument.Parse(diagnosticsJson))
        {
            JsonElement root = diagnosticsDocument.RootElement;
            string mediaPath = root.GetProperty("MediaPath").GetString()!;
            string projectRootPath = root.GetProperty("ProjectRootPath").GetString()!;
            Assert.DoesNotContain(
                $@"C:\Users\{Environment.UserName}\",
                mediaPath,
                StringComparison.OrdinalIgnoreCase);
            Assert.Contains(@"C:\Users\<USER>\", mediaPath, StringComparison.Ordinal);
            Assert.DoesNotContain(Environment.UserName, projectRootPath, StringComparison.OrdinalIgnoreCase);
        }

        string logContent = await ReadEntryAsync(archive, "logs/trackdub.log");
        Assert.DoesNotContain(Environment.UserName, logContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Users\<USER>\AppData", logContent, StringComparison.Ordinal);

        string schemaVersion = await ReadEntryAsync(archive, "schema-version.txt");
        Assert.Equal("20", schemaVersion);

        using (JsonDocument diagnosticsDocument = JsonDocument.Parse(diagnosticsJson))
        {
            int exportedCategory = diagnosticsDocument.RootElement.GetProperty("FailureCategory").GetInt32();
            Assert.Equal((int)FailureCategory.UnknownError, exportedCategory);
        }
    }

    [Fact]
    public async Task ExportBundleAsync_truncates_session_logs_to_last_configured_bytes()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);
        await File.WriteAllTextAsync(storagePaths.LogFilePath, "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ", TestContext.Current.CancellationToken);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore, maxSessionLogBytes: 8);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        string logContent = await ReadEntryAsync(archive, "logs/trackdub.log");
        Assert.Equal("STUVWXYZ", logContent);
    }

    [Fact]
    public async Task ExportBundleAsync_writes_unavailable_schema_when_project_database_has_no_schema_table()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        string projectRoot = Path.Combine(testRoot, "project");
        Directory.CreateDirectory(projectRoot);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = Path.Combine(projectRoot, ProjectArtifactPaths.DatabaseFileName),
            ForeignKeys = true,
            Pooling = false
        }.ConnectionString))
        {
            await connection.OpenAsync();
        }

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath, ProjectRootPath: projectRoot));

        using var archive = ZipFile.OpenRead(outputPath);
        string schemaVersion = await ReadEntryAsync(archive, "schema-version.txt");
        Assert.Equal("unavailable", schemaVersion);
    }

    [Fact]
    public async Task ExportBundleAsync_writes_unavailable_schema_when_project_path_is_invalid()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);

        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(
            outputPath,
            ProjectRootPath: string.Concat("bad", '\0', "path")));

        using var archive = ZipFile.OpenRead(outputPath);
        string schemaVersion = await ReadEntryAsync(archive, "schema-version.txt");
        Assert.Equal("unavailable", schemaVersion);
    }

    [Fact]
    public async Task ExportBundleAsync_writes_empty_model_cache_summary_when_cache_index_is_malformed()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.ModelCacheDirectory);
        await File.WriteAllTextAsync(storagePaths.ModelCacheIndexPath, "{ not valid json", TestContext.Current.CancellationToken);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        string modelCacheSummary = await ReadEntryAsync(archive, "model-cache-summary.json");
        Assert.Contains("\"TotalEntries\": 0", modelCacheSummary, StringComparison.Ordinal);
        Assert.Contains("\"Entries\": []", modelCacheSummary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ExportBundleAsync_marks_file_based_model_cache_root_as_installed()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        string modelFilePath = Path.Combine(testRoot, "model.onnx");
        await File.WriteAllTextAsync(modelFilePath, "weights", TestContext.Current.CancellationToken);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        await recordStore.SaveAsync(
        [
            new LocalModelCacheRecord("file-model", modelFilePath, "main", "sha", DateTimeOffset.UtcNow)
        ]);

        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        string modelCacheSummary = await ReadEntryAsync(archive, "model-cache-summary.json");
        using JsonDocument document = JsonDocument.Parse(modelCacheSummary);
        Assert.Equal(1, document.RootElement.GetProperty("InstalledEntries").GetInt32());
        Assert.Equal("installed", document.RootElement.GetProperty("Entries")[0].GetProperty("Status").GetString());
    }

    [Fact]
    public async Task ExportBundleAsync_continues_when_session_log_cannot_be_read()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);
        await File.WriteAllTextAsync(storagePaths.LogFilePath, "log content", TestContext.Current.CancellationToken);

        await using var lockedLog = new FileStream(
            storagePaths.LogFilePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        Assert.NotNull(archive.GetEntry("diagnostics.json"));
        Assert.NotNull(archive.GetEntry("model-cache-summary.json"));
    }

    [Fact]
    public async Task ExportBundleAsync_orders_archive_logs_by_numeric_index_then_active_log()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        string logDirectory = Path.GetDirectoryName(storagePaths.LogFilePath)!;
        Directory.CreateDirectory(logDirectory);
        await File.WriteAllTextAsync(storagePaths.LogFilePath, "active", TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(logDirectory, "trackdub.10.log"),
            "archive 10",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(logDirectory, "trackdub.2.log"),
            "archive 2",
            TestContext.Current.CancellationToken);
        await File.WriteAllTextAsync(
            Path.Combine(logDirectory, "trackdub.1.log"),
            "archive 1",
            TestContext.Current.CancellationToken);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        string diagnosticsJson = await ReadEntryAsync(archive, "diagnostics.json");
        using JsonDocument document = JsonDocument.Parse(diagnosticsJson);
        string[] logFiles = document.RootElement
            .GetProperty("LogFiles")
            .EnumerateArray()
            .Select(element => element.GetString())
            .ToArray()!;

        Assert.Equal(
            ["logs/trackdub.1.log", "logs/trackdub.2.log", "logs/trackdub.10.log", "logs/trackdub.log"],
            logFiles);
    }

    [Fact]
    public async Task ExportBundleAsync_does_not_claim_directml_without_runtime_probe()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        Assert.False(await ReadDirectMlRuntimeRouteAvailableAsync(archive));
    }

    [Fact]
    public async Task ExportBundleAsync_reports_directml_from_runtime_probe()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(
            storagePaths,
            recordStore,
            runtimeInfo: new FakeDiagnosticsRuntimeInfo(DirectMlAvailable: true));
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        Assert.True(await ReadDirectMlRuntimeRouteAvailableAsync(archive));
    }

    [Fact]
    public async Task ExportBundleAsync_redacts_terminal_user_profile_paths()
    {
        string testRoot = CreateTempDirectory();
        var storagePaths = new TrackdubStoragePaths(testRoot);
        Directory.CreateDirectory(storagePaths.RootDirectory);
        await File.WriteAllTextAsync(
            storagePaths.LogFilePath,
            $@"Terminal Windows path C:\Users\{Environment.UserName} and Unix path /Users/{Environment.UserName}",
            TestContext.Current.CancellationToken);

        var recordStore = new LocalModelCacheRecordStore(storagePaths);
        string outputPath = Path.Combine(testRoot, "diagnostics.zip");
        var exporter = new DiagnosticsBundleExporter(storagePaths, recordStore);
        await exporter.ExportBundleAsync(new DiagnosticsBundleExportRequest(outputPath));

        using var archive = ZipFile.OpenRead(outputPath);
        string logContent = await ReadEntryAsync(archive, "logs/trackdub.log");
        Assert.DoesNotContain(Environment.UserName, logContent, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\Users\<USER>", logContent, StringComparison.Ordinal);
        Assert.Contains("/Users/<USER>", logContent, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        foreach (string directory in tempDirectories)
        {
            try
            {
                if (Directory.Exists(directory))
                {
                    Directory.Delete(directory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup.
            }
        }
    }

    private static async Task CreateProjectSchemaVersionAsync(string projectRootPath, int version)
    {
        string databasePath = Path.Combine(projectRootPath, ProjectArtifactPaths.DatabaseFileName);
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            ForeignKeys = true,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            """
            CREATE TABLE IF NOT EXISTS project_schema_versions (
                version INTEGER NOT NULL
            );
            INSERT INTO project_schema_versions(version) VALUES (@version);
            """;
        command.Parameters.AddWithValue("@version", version);
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<string> ReadEntryAsync(ZipArchive archive, string path)
    {
        ZipArchiveEntry entry = archive.GetEntry(path) ?? throw new InvalidOperationException($"Zip entry '{path}' not found.");
        await using Stream stream = entry.Open();
        using var reader = new StreamReader(stream);
        string text = await reader.ReadToEndAsync();
        return text.TrimEnd('\r', '\n');
    }

    private static async Task<bool> ReadDirectMlRuntimeRouteAvailableAsync(ZipArchive archive)
    {
        string hardwareInfoJson = await ReadEntryAsync(archive, "hardware-info.json");
        using JsonDocument document = JsonDocument.Parse(hardwareInfoJson);
        return document.RootElement.GetProperty("DirectMlRuntimeRouteAvailable").GetBoolean();
    }

    private string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }

    private sealed record FakeDiagnosticsRuntimeInfo(bool DirectMlAvailable) : IDiagnosticsRuntimeInfo
    {
        public string? GpuDescription => null;

        public string? OnnxRuntimeVersion => null;

        public string? WindowsAppSdkVersion => null;

        public bool MigraphxAvailable => false;

        public string? MigraphxReadinessDetail => null;
    }
}
