namespace Trackdub.Infrastructure.Persistence.Sqlite;

internal static class SqliteValueConverters
{
    public static string ToDbValue(Guid value) => value.ToString("D");

    public static string ToDbValue(DateTimeOffset value) => value.ToString("O");

    public static Guid ParseGuid(string value) => Guid.Parse(value);

    public static DateTimeOffset ParseDateTimeOffset(string value) =>
        DateTimeOffset.Parse(value, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.RoundtripKind);
}
