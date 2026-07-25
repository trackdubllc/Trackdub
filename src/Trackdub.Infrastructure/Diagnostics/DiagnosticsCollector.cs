using System.Runtime.InteropServices;
using Trackdub.Contracts.Diagnostics;
using Trackdub.Domain;
using Trackdub.Infrastructure.Persistence.Repositories;
using Trackdub.Infrastructure.Persistence.Sqlite;
using Trackdub.Infrastructure.Settings;

namespace Trackdub.Infrastructure.Diagnostics;

/// <summary>
/// Aggregates structured diagnostics information from the local installation.
/// Collects log files, DB schema version, model cache state, hardware profile, OS version,
/// and runtime version info into a single <see cref="DiagnosticsSnapshot"/>.
/// </summary>
public sealed class DiagnosticsCollector(
    TrackdubStoragePaths storagePaths,
    LocalModelCacheRecordStore modelCacheStore,
    IDiagnosticsRuntimeInfo? runtimeInfo = null)
    : IDiagnosticsCollector
{
    private readonly TrackdubStoragePaths storagePaths = storagePaths ?? throw new ArgumentNullException(nameof(storagePaths));
    private readonly LocalModelCacheRecordStore modelCacheStore = modelCacheStore ?? throw new ArgumentNullException(nameof(modelCacheStore));

    /// <inheritdoc />
    public async Task<DiagnosticsSnapshot> CollectAsync(CancellationToken cancellationToken = default)
    {
        string osVersion = RuntimeInformation.OSDescription;
        string architecture = RuntimeInformation.ProcessArchitecture.ToString();

        IReadOnlyList<ModelCacheEntry> modelEntries = await CollectModelCacheEntriesAsync(cancellationToken)
            .ConfigureAwait(false);
        IReadOnlyList<string> logFilePaths = CollectLogFilePaths();
        int dbSchemaVersion = SqliteProjectSchemaMigrations.All.Max(m => m.Version);

        return new DiagnosticsSnapshot(
            OsVersion: osVersion,
            Architecture: architecture,
            GpuDescription: runtimeInfo?.GpuDescription,
            DirectMlAvailable: runtimeInfo?.DirectMlAvailable ?? false,
            OnnxRuntimeVersion: runtimeInfo?.OnnxRuntimeVersion,
            WindowsAppSdkVersion: runtimeInfo?.WindowsAppSdkVersion,
            DbSchemaVersion: dbSchemaVersion,
            ModelCacheEntries: modelEntries,
            LogFilePaths: logFilePaths,
            CollectedAtUtc: DateTimeOffset.UtcNow);
    }

    private async Task<IReadOnlyList<ModelCacheEntry>> CollectModelCacheEntriesAsync(CancellationToken cancellationToken)
    {
        IReadOnlyList<LocalModelCacheRecord> records = await modelCacheStore
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);

        if (records.Count == 0)
        {
            return [];
        }

        var entries = new List<ModelCacheEntry>(records.Count);
        foreach (var record in records)
        {
            entries.Add(DetermineModelCacheEntry(record));
        }

        return entries;
    }

    private static ModelCacheEntry DetermineModelCacheEntry(LocalModelCacheRecord record)
    {
        if (!Directory.Exists(record.RootPath) && !File.Exists(record.RootPath))
        {
            return new ModelCacheEntry(record.ModelId, Contracts.Diagnostics.ModelCacheState.Missing);
        }

        if (Directory.Exists(record.RootPath))
        {
            try
            {
                return Directory.EnumerateFileSystemEntries(record.RootPath).Any()
                    ? new ModelCacheEntry(record.ModelId, Contracts.Diagnostics.ModelCacheState.Installed)
                    : new ModelCacheEntry(record.ModelId, Contracts.Diagnostics.ModelCacheState.Corrupt, "Model cache directory is empty.");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return new ModelCacheEntry(record.ModelId, Contracts.Diagnostics.ModelCacheState.Corrupt, ex.Message);
            }
        }

        return new ModelCacheEntry(record.ModelId, Contracts.Diagnostics.ModelCacheState.Installed);
    }

    private IReadOnlyList<string> CollectLogFilePaths()
    {
        string? logDirectory = Path.GetDirectoryName(Path.GetFullPath(storagePaths.LogFilePath));
        if (string.IsNullOrWhiteSpace(logDirectory) || !Directory.Exists(logDirectory))
        {
            return [];
        }

        var logFiles = new List<string>();

        // Active log file
        if (File.Exists(storagePaths.LogFilePath))
        {
            logFiles.Add(storagePaths.LogFilePath);
        }

        // Archive log files: trackdub.1.log, trackdub.2.log, etc.
        foreach (string archivePath in Directory.EnumerateFiles(
            logDirectory,
            $"{Path.GetFileNameWithoutExtension(storagePaths.LogFilePath)}.*{Path.GetExtension(storagePaths.LogFilePath)}",
            SearchOption.TopDirectoryOnly))
        {
            logFiles.Add(archivePath);
        }

        return LogFileOrdering.OrderByRotation(
                logFiles.Distinct(StringComparer.OrdinalIgnoreCase),
                storagePaths.LogFilePath)
            .ToArray();
    }
}
