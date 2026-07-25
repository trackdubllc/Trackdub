using System.Data.Common;
using Trackdub.Contracts;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

public sealed class ScopedSqliteConnectionProvider : IScopedConnectionProvider
{
    private readonly SqliteConnection connection;
    private bool disposed;

    public ScopedSqliteConnectionProvider(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string fullDatabasePath = Path.GetFullPath(databasePath);
        string? directory = Path.GetDirectoryName(fullDatabasePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = fullDatabasePath,
            ForeignKeys = true,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = true
        };

        connection = new SqliteConnection(builder.ConnectionString);
        connection.Open();

        using var pragmaCommand = connection.CreateCommand();
        pragmaCommand.CommandText = "PRAGMA foreign_keys = ON; PRAGMA journal_mode = WAL; PRAGMA synchronous = NORMAL;";
        pragmaCommand.ExecuteNonQuery();
    }

    public DbConnection Connection => connection;

    public void Dispose()
    {
        if (!disposed)
        {
            connection.Dispose();
            disposed = true;
        }
    }
}
