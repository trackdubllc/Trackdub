using System.Text;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

/// <summary>
/// Base exception for failures detected during project database startup (open, migration, or integrity validation).
/// Callers should inspect the derived type and the <see cref="BackupPath"/> property (when available) to
/// surface a recovery-oriented message to the user.
/// </summary>
public class ProjectDatabaseException : Exception
{
    /// <summary>
    /// Path to a pre-migration backup of the database, if one was created before the failure occurred.
    /// May be <see langword="null"/> when no backup was taken (e.g., fresh database or backup was not triggered).
    /// </summary>
    public string? BackupPath { get; }

    protected ProjectDatabaseException(string message, string? backupPath = null)
        : base(message)
    {
        BackupPath = backupPath;
    }

    protected ProjectDatabaseException(string message, Exception innerException, string? backupPath = null)
        : base(message, innerException)
    {
        BackupPath = backupPath;
    }
}

/// <summary>
/// Thrown when the project database schema version is newer than the version supported by this build.
/// The user must update Trackdub before opening the project.
/// </summary>
public sealed class ProjectDatabaseSchemaVersionException(int schemaVersion, int maxSupportedVersion)
    : ProjectDatabaseException(
        $"Project schema version {schemaVersion} is newer than this build supports ({maxSupportedVersion}). " +
        "Update Trackdub before opening this project.")
{
    /// <summary>The schema version recorded in the database.</summary>
    public int SchemaVersion { get; } = schemaVersion;

    /// <summary>The highest schema version supported by the current build.</summary>
    public int MaxSupportedVersion { get; } = maxSupportedVersion;
}

/// <summary>
/// Thrown when the project database is determined to be corrupted after startup operations, such as when
/// a SQLite integrity check fails or SQLite reports a corruption-related error.
/// The <see cref="ProjectDatabaseException.BackupPath"/> property points to a pre-migration backup if one
/// was created, which may be used to recover data.
/// </summary>
public sealed class ProjectDatabaseCorruptedException : ProjectDatabaseException
{
    /// <summary>The path to the database file reported as corrupted.</summary>
    public string DatabasePath { get; }

    /// <summary>
    /// Details describing the corruption failure, such as raw <c>PRAGMA integrity_check</c> output or an
    /// underlying SQLite error message.
    /// </summary>
    public string FailureDetails { get; }

    /// <summary>
    /// Compatibility alias for <see cref="FailureDetails"/>. This value may contain either raw
    /// <c>PRAGMA integrity_check</c> output or an underlying SQLite error message.
    /// </summary>
    public string IntegrityCheckResult => FailureDetails;

    public ProjectDatabaseCorruptedException(
        string databasePath,
        string failureDetails,
        string? backupPath = null)
        : base(BuildMessage(databasePath, failureDetails, backupPath), backupPath)
    {
        DatabasePath = databasePath;
        FailureDetails = failureDetails;
    }

    public ProjectDatabaseCorruptedException(
        string databasePath,
        string failureDetails,
        Exception innerException,
        string? backupPath = null)
        : base(BuildMessage(databasePath, failureDetails, backupPath), innerException, backupPath)
    {
        DatabasePath = databasePath;
        FailureDetails = failureDetails;
    }

    private static string BuildMessage(string databasePath, string failureDetails, string? backupPath)
    {
        var sb = new StringBuilder();
        sb.Append($"Project database reported corruption for '{databasePath}': {failureDetails}.");

        if (backupPath is not null)
        {
            sb.Append($" A backup was saved to '{backupPath}' before the last migration attempt.");
        }

        sb.Append(" To recover: restore the backup, repair the database with a SQLite tool, or create a new project.");
        return sb.ToString();
    }
}
