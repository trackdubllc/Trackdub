using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Logging;
using Trackdub.Infrastructure.Settings;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

/// <summary>
/// SQLite-backed implementation of the per-session voice-cloning consent gate. Each application session starts with consent NOT granted; the SQLite store persists grant/clear events as an audit trail and does not pre-grant consent on startup.
/// </summary>
public sealed class SqliteConsentService : IConsentService, IDisposable
{
    private readonly string databasePath;
    private readonly IApplicationLogger logger;
    private readonly object gate = new();
    private bool isVoiceCloningConsentGranted;

    public Guid SessionId { get; } = Guid.NewGuid();

    public bool IsVoiceCloningConsentGranted
    {
        get
        {
            lock (gate)
            {
                return isVoiceCloningConsentGranted;
            }
        }
    }

    public event EventHandler? VoiceCloningConsentChanged;

    public SqliteConsentService(TrackdubStoragePaths storagePaths, IApplicationLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(storagePaths);
        databasePath = Path.Combine(storagePaths.RootDirectory, "consent.db");
        this.logger = logger ?? new DebugApplicationLogger();
    }

    public void GrantVoiceCloningConsent()
    {
        bool changed;
        lock (gate)
        {
            changed = !isVoiceCloningConsentGranted;
            isVoiceCloningConsentGranted = true;
            try
            {
                SaveToDatabase(granted: true);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"Failed to persist voice cloning consent grant: {ex.Message}", ex);
            }
        }

        if (changed)
        {
            VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    public void ClearVoiceCloningConsent()
    {
        bool changed;
        lock (gate)
        {
            changed = isVoiceCloningConsentGranted;
            isVoiceCloningConsentGranted = false;
            try
            {
                SaveToDatabase(granted: false);
            }
            catch (Exception ex)
            {
                this.logger.LogWarning($"Failed to persist voice cloning consent clear: {ex.Message}", ex);
            }
        }

        if (changed)
        {
            VoiceCloningConsentChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void SaveToDatabase(bool granted)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath)!);

        using SqliteConnection connection = OpenConnection();

        using (SqliteCommand createCommand = connection.CreateCommand())
        {
            createCommand.CommandText =
                """
                CREATE TABLE IF NOT EXISTS session_consent (
                    id INTEGER PRIMARY KEY NOT NULL,
                    granted INTEGER NOT NULL,
                    granted_at_utc TEXT NOT NULL,
                    updated_at_utc TEXT NOT NULL
                );
                """;
            createCommand.ExecuteNonQuery();
        }

        using SqliteCommand upsertCommand = connection.CreateCommand();
        upsertCommand.CommandText =
            """
            INSERT INTO session_consent (id, granted, granted_at_utc, updated_at_utc)
            VALUES (1, $granted, $now, $now)
            ON CONFLICT(id) DO UPDATE SET
                granted = $granted,
                updated_at_utc = $now;
            """;
        string now = DateTimeOffset.UtcNow.ToString("O");
        upsertCommand.Parameters.AddWithValue("$granted", granted ? 1 : 0);
        upsertCommand.Parameters.AddWithValue("$now", now);
        upsertCommand.ExecuteNonQuery();
    }

    private SqliteConnection OpenConnection()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Pooling = false
        };

        var connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();
        return connection;
    }

    public void Dispose()
    {
    }
}
