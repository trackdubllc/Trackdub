namespace Trackdub.Cli;

/// <summary>
/// Normalizes filesystem paths pasted from Explorer "Copy as path" or drag-and-drop into a terminal.
/// Those sources wrap the path in quotes, and PowerShell often prefixes the call operator.
/// </summary>
internal static class UserPathText
{
    internal static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        string path = value.Trim().Trim('\uFEFF');
        path = StripPowerShellCallOperator(path);
        path = UnwrapMatchingQuotes(path);
        return path.Trim();
    }

    internal static string? NormalizeOptional(string? value)
    {
        string normalized = Normalize(value);
        return string.IsNullOrWhiteSpace(normalized) ? null : normalized;
    }

    private static string StripPowerShellCallOperator(string path)
    {
        if (path.Length < 2 || path[0] != '&')
        {
            return path;
        }

        char second = path[1];
        if (char.IsWhiteSpace(second) || IsQuote(second))
        {
            return path[1..].Trim();
        }

        return path;
    }

    private static string UnwrapMatchingQuotes(string path)
    {
        for (int i = 0; i < 3 && path.Length >= 2; i++)
        {
            if (!IsQuotePair(path[0], path[^1]))
            {
                break;
            }

            path = path[1..^1].Trim();
        }

        return path;
    }

    private static bool IsQuote(char value) =>
        value is '"' or '\'' or '\u201C' or '\u201D' or '\u2018' or '\u2019';

    private static bool IsQuotePair(char start, char end) =>
        (start, end) switch
        {
            ('"', '"') => true,
            ('\'', '\'') => true,
            ('\u201C', '\u201D') => true,
            ('\u201C', '"') => true,
            ('"', '\u201D') => true,
            ('\u2018', '\u2019') => true,
            ('\u2018', '\'') => true,
            ('\'', '\u2019') => true,
            _ => false,
        };
}
