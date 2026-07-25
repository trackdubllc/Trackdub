using System.Globalization;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Trackdub.Application.Transcripts.Pipeline;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Contracts.Projects;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Diagnostics;

public sealed class DiagnosticsBundleExporter : IDiagnosticsBundleExporter
{
    public const long DefaultMaxSessionLogBytes = 50L * 1024L * 1024L;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly TrackdubStoragePaths storagePaths;
    private readonly LocalModelCacheRecordStore modelCacheRecordStore;
    private readonly long maxSessionLogBytes;
    private readonly IDiagnosticsRuntimeInfo? runtimeInfo;

    /// <summary>
    /// Composition-singleton transient-fault bus injected so the diagnostics bundle can read
    /// the live stream when <see cref="DiagnosticsBundleExportRequest.Transient"/> is null.
    /// Pulled from Composition's <c>services.AddSingleton&lt;PipelineTransientFaultBus&gt;()</c>
    /// registration (spec §4.4 follow-up lane C8).
    /// </summary>
    public PipelineTransientFaultBus? TransientFaultBus { get; }

    public DiagnosticsBundleExporter(
        TrackdubStoragePaths storagePaths,
        LocalModelCacheRecordStore modelCacheRecordStore,
        long maxSessionLogBytes = DefaultMaxSessionLogBytes,
        IDiagnosticsRuntimeInfo? runtimeInfo = null,
        PipelineTransientFaultBus? transientFaultBus = null)
    {
        this.storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
        this.modelCacheRecordStore = modelCacheRecordStore ?? throw new ArgumentNullException(nameof(modelCacheRecordStore));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxSessionLogBytes);
        this.maxSessionLogBytes = maxSessionLogBytes;
        this.runtimeInfo = runtimeInfo;
        TransientFaultBus = transientFaultBus;
    }

    public async Task ExportBundleAsync(DiagnosticsBundleExportRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentException.ThrowIfNullOrWhiteSpace(request.DestinationZipPath);

        string destinationZipPath = Path.GetFullPath(request.DestinationZipPath);
        string? outputDirectory = Path.GetDirectoryName(destinationZipPath);
        if (!string.IsNullOrWhiteSpace(outputDirectory))
        {
            Directory.CreateDirectory(outputDirectory);
        }

        await using var outputStream = new FileStream(
            destinationZipPath,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        using var archive = new ZipArchive(outputStream, ZipArchiveMode.Create, leaveOpen: false);

        IReadOnlyList<string> includedLogs = await AddLogEntriesAsync(archive, cancellationToken).ConfigureAwait(false);
        int? schemaVersion = await ReadSchemaVersionAsync(request.ProjectRootPath, cancellationToken).ConfigureAwait(false);
        ModelCacheSummary cacheSummary = await BuildModelCacheSummaryAsync(cancellationToken).ConfigureAwait(false);
        HardwareInfo hardwareInfo = BuildHardwareInfo();

        string modelCacheSummaryJson = RedactPaths(JsonSerializer.Serialize(cacheSummary, JsonOptions));
        string hardwareInfoJson = RedactPaths(JsonSerializer.Serialize(hardwareInfo, JsonOptions));
        await AddTextEntryAsync(archive, "model-cache-summary.json", modelCacheSummaryJson, cancellationToken).ConfigureAwait(false);
        await AddTextEntryAsync(archive, "hardware-info.json", hardwareInfoJson, cancellationToken).ConfigureAwait(false);
        await AddTextEntryAsync(
            archive,
            "schema-version.txt",
            schemaVersion?.ToString(CultureInfo.InvariantCulture) ?? "unavailable",
            cancellationToken).ConfigureAwait(false);

        // Contract for ExportBundleAsync on AvON contracts/IDiagnostics
        var manifest = new DiagnosticsManifest(
            DateTimeOffset.UtcNow,
            schemaVersion,
            includedLogs,
            cacheSummary,
            hardwareInfo,
            request.FailureCategory,
            request.FailureExplanation,
            request.FailureContext,
            request.ProjectRootPath,
            request.MediaPath);
        string diagnosticsJson = RedactPaths(JsonSerializer.Serialize(manifest, JsonOptions));
        await AddTextEntryAsync(archive, "diagnostics.json", diagnosticsJson, cancellationToken).ConfigureAwait(false);

        TransientFaultSummary? transientSummary = request.Transient;
        if (transientSummary is null && TransientFaultBus is not null)
        {
            transientSummary = TransientFaultSummary.From(TransientFaultBus.Snapshot());
        }

        if (transientSummary is not null)
        {
            string transientJson = RedactPaths(JsonSerializer.Serialize(transientSummary, JsonOptions));
            await AddTextEntryAsync(archive, "transient-fault-summary.json", transientJson, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task<IReadOnlyList<string>> AddLogEntriesAsync(ZipArchive archive, CancellationToken cancellationToken)
    {
        var includedEntries = new List<string>();
        foreach (string logPath in EnumerateLogFiles())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!File.Exists(logPath))
            {
                continue;
            }

            byte[] bytes;
            try
            {
                bytes = await ReadTailBytesAsync(logPath, maxSessionLogBytes, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                continue;
            }

            string entryPath = $"logs/{Path.GetFileName(logPath)}";
            string redactedContent = RedactPaths(Encoding.UTF8.GetString(bytes));
            await AddTextEntryAsync(archive, entryPath, redactedContent, cancellationToken).ConfigureAwait(false);
            includedEntries.Add(entryPath);
        }

        return includedEntries;
    }

    private IEnumerable<string> EnumerateLogFiles()
    {
        string primaryLogPath = storagePaths.LogFilePath;
        var logFiles = new List<string>();
        if (File.Exists(primaryLogPath))
        {
            logFiles.Add(primaryLogPath);
        }

        string directory = Path.GetDirectoryName(primaryLogPath) ?? string.Empty;
        if (!Directory.Exists(directory))
        {
            return logFiles;
        }

        string fileNameWithoutExtension = Path.GetFileNameWithoutExtension(primaryLogPath);
        string extension = Path.GetExtension(primaryLogPath);
        foreach (string archivePath in Directory.EnumerateFiles(directory, $"{fileNameWithoutExtension}.*{extension}")
                     .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (string.Equals(archivePath, primaryLogPath, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            logFiles.Add(archivePath);
        }

        return LogFileOrdering.OrderByRotation(
            logFiles.Distinct(StringComparer.OrdinalIgnoreCase),
            primaryLogPath);
    }

    private static async Task<byte[]> ReadTailBytesAsync(string path, long maxBytes, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            bufferSize: 4096,
            options: FileOptions.Asynchronous);
        long bytesToRead = Math.Min(stream.Length, maxBytes);
        if (bytesToRead > int.MaxValue)
        {
            throw new InvalidOperationException("Diagnostics log tail is too large to buffer.");
        }

        stream.Seek(-bytesToRead, SeekOrigin.End);
        byte[] buffer = new byte[(int)bytesToRead];
        int offset = 0;
        while (offset < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, buffer.Length - offset), cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }

            offset += read;
        }

        return offset == buffer.Length ? buffer : buffer[..offset];
    }

    private static async Task AddTextEntryAsync(ZipArchive archive, string entryPath, string content, CancellationToken cancellationToken)
    {
        ZipArchiveEntry entry = archive.CreateEntry(entryPath, CompressionLevel.Optimal);
        await using Stream entryStream = entry.Open();
        await using var writer = new StreamWriter(entryStream, Encoding.UTF8, bufferSize: 1024, leaveOpen: false);
        await writer.WriteAsync(content.AsMemory(), cancellationToken).ConfigureAwait(false);
    }

    private async Task<int?> ReadSchemaVersionAsync(string? projectRootPath, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(projectRootPath))
        {
            return null;
        }

        try
        {
            string databasePath = Path.Combine(Path.GetFullPath(projectRootPath), ProjectArtifactPaths.DatabaseFileName);
            if (!File.Exists(databasePath))
            {
                return null;
            }

            var connectionString = new SqliteConnectionStringBuilder
            {
                DataSource = databasePath,
                ForeignKeys = true,
                Pooling = false
            }.ConnectionString;
            await using var connection = new SqliteConnection(connectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT COALESCE(MAX(version), 0) FROM project_schema_versions;";
            object? scalar = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
            return scalar is null or DBNull
                ? null
                : Convert.ToInt32(scalar, CultureInfo.InvariantCulture);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException or UnauthorizedAccessException or SqliteException)
        {
            return null;
        }
    }

    private async Task<ModelCacheSummary> BuildModelCacheSummaryAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalModelCacheRecord> records;
        try
        {
            records = await modelCacheRecordStore.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return CreateEmptyModelCacheSummary();
        }

        ModelCacheEntrySummary[] entries = records
            .Select(record => new ModelCacheEntrySummary(
                record.ModelId,
                record.Revision,
                record.RootPath,
                ResolveCacheStatus(record.RootPath)))
            .ToArray();

        return new ModelCacheSummary(
            entries.Length,
            entries.Count(entry => string.Equals(entry.Status, "installed", StringComparison.OrdinalIgnoreCase)),
            entries.Count(entry => string.Equals(entry.Status, "missing", StringComparison.OrdinalIgnoreCase)),
            entries.Count(entry => string.Equals(entry.Status, "corrupt", StringComparison.OrdinalIgnoreCase)),
            entries);
    }

    private static ModelCacheSummary CreateEmptyModelCacheSummary() =>
        new(
            TotalEntries: 0,
            InstalledEntries: 0,
            MissingEntries: 0,
            CorruptEntries: 0,
            Entries: []);

    private static string ResolveCacheStatus(string rootPath)
    {
        if (File.Exists(rootPath))
        {
            return "installed";
        }

        if (!Directory.Exists(rootPath))
        {
            return "missing";
        }

        try
        {
            return Directory.EnumerateFileSystemEntries(rootPath).Any() ? "installed" : "corrupt";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return "corrupt";
        }
    }

    private HardwareInfo BuildHardwareInfo()
    {
        string? onnxRuntimeVersion = !string.IsNullOrEmpty(runtimeInfo?.OnnxRuntimeVersion)
            ? runtimeInfo.OnnxRuntimeVersion
            : Type
                .GetType("Microsoft.ML.OnnxRuntime.InferenceSession, Microsoft.ML.OnnxRuntime", throwOnError: false)
                ?.Assembly
                .GetName()
                .Version
                ?.ToString();

        return new HardwareInfo(
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString(),
            RuntimeInformation.ProcessArchitecture.ToString(),
            Environment.Version.ToString(),
            onnxRuntimeVersion,
            runtimeInfo?.DirectMlAvailable ?? false);
    }

    private static string RedactPaths(string input) =>
        UserProfilePathRedactor.Redact(input);

    private sealed record DiagnosticsManifest(
        DateTimeOffset CreatedAtUtc,
        int? ProjectSchemaVersion,
        IReadOnlyList<string> LogFiles,
        ModelCacheSummary ModelCacheSummary,
        HardwareInfo Hardware,
        FailureCategory? FailureCategory,
        string? FailureExplanation,
        string? FailureContext,
        string? ProjectRootPath,
        string? MediaPath);

    private sealed record ModelCacheSummary(
        int TotalEntries,
        int InstalledEntries,
        int MissingEntries,
        int CorruptEntries,
        IReadOnlyList<ModelCacheEntrySummary> Entries);

    private sealed record ModelCacheEntrySummary(
        string ModelId,
        string Revision,
        string RootPath,
        string Status);

    private sealed record HardwareInfo(
        string OperatingSystem,
        string OsArchitecture,
        string ProcessArchitecture,
        string DotnetVersion,
        string? OnnxRuntimeVersion,
        bool DirectMlRuntimeRouteAvailable);
}
