using System.Text.RegularExpressions;

namespace Trackdub.Infrastructure.Persistence.Sqlite;

internal static partial class SqliteIdentifierHelper
{
    [GeneratedRegex("^[A-Za-z_][A-Za-z0-9_]*$", RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex("^[A-Za-z0-9_ ',().-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex ColumnDefinitionPattern();

    internal static string QuoteIdentifier(string identifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(identifier);
        if (!IdentifierPattern().IsMatch(identifier))
        {
            throw new ArgumentException($"Invalid SQLite identifier '{identifier}'.", nameof(identifier));
        }

        return "[" + identifier + "]";
    }

    internal static string BuildAlterTableAddColumn(
        string tableName,
        string columnName,
        string columnDefinition)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(columnDefinition);
        if (!ColumnDefinitionPattern().IsMatch(columnDefinition))
        {
            throw new ArgumentException($"Invalid SQLite column definition '{columnDefinition}'.", nameof(columnDefinition));
        }

        return "ALTER TABLE "
            + QuoteIdentifier(tableName)
            + " ADD COLUMN "
            + QuoteIdentifier(columnName)
            + " "
            + columnDefinition
            + ";";
    }

    internal static string BuildPragmaTableInfo(string tableName) =>
        "PRAGMA table_info(" + QuoteIdentifier(tableName) + ");";
}
