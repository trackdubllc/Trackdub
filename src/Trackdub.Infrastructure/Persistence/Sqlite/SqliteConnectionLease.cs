using System.Data;
using Trackdub.Contracts;
using Microsoft.Data.Sqlite;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

internal readonly struct SqliteConnectionLease : IAsyncDisposable
{
    private readonly SqliteConnection? ownedConnection;

    private SqliteConnectionLease(SqliteConnection connection, bool ownsConnection)
    {
        Connection = connection;
        ownedConnection = ownsConnection ? connection : null;
    }

    public SqliteConnection Connection { get; }

    public static async Task<SqliteConnectionLease> OpenAsync(
        SqliteProjectDatabase database,
        IScopedConnectionProvider? scopedConnectionProvider,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(database);

        if (scopedConnectionProvider is null)
        {
            SqliteConnection ownedConnection = await database.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            return new SqliteConnectionLease(ownedConnection, ownsConnection: true);
        }

        if (scopedConnectionProvider.Connection is not SqliteConnection scopedConnection)
        {
            throw new InvalidOperationException("The scoped connection provider must provide a SqliteConnection.");
        }

        if (scopedConnection.State is not ConnectionState.Open)
        {
            await scopedConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
            await SqliteProjectDatabase.EnableForeignKeysAsync(scopedConnection, cancellationToken).ConfigureAwait(false);
        }

        return new SqliteConnectionLease(scopedConnection, ownsConnection: false);
    }

    public ValueTask DisposeAsync() =>
        ownedConnection is null
            ? ValueTask.CompletedTask
            : ownedConnection.DisposeAsync();
}
