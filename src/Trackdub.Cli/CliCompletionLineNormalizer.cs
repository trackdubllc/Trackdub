namespace Trackdub.Cli;

/// <summary>
/// Normalizes shell command lines for System.CommandLine completion parsing.
/// </summary>
internal static class CliCompletionLineNormalizer
{
    internal static (string Line, int Position) Normalize(string commandLine, int cursorPosition, string? executableName)
    {
        if (string.IsNullOrEmpty(commandLine))
        {
            return (commandLine, cursorPosition);
        }

        int firstTokenEnd = IndexOfFirstTokenEnd(commandLine);
        if (firstTokenEnd <= 0)
        {
            return (commandLine, cursorPosition);
        }

        string firstToken = commandLine[..firstTokenEnd];
        if (!MatchesExecutable(firstToken, executableName))
        {
            return (commandLine, cursorPosition);
        }

        int stripLength = firstTokenEnd;
        while (stripLength < commandLine.Length && char.IsWhiteSpace(commandLine[stripLength]))
        {
            stripLength++;
        }

        string normalizedLine = commandLine[stripLength..];
        int normalizedPosition = Math.Max(0, cursorPosition - stripLength);
        normalizedPosition = Math.Min(normalizedPosition, normalizedLine.Length);

        return (normalizedLine, normalizedPosition);
    }

    private static bool MatchesExecutable(string firstToken, string? executableName)
    {
        if (string.IsNullOrWhiteSpace(executableName))
        {
            return false;
        }

        string tokenBaseName = Path.GetFileName(firstToken.Trim('"'));
        string expectedBaseName = Path.GetFileName(executableName.Trim('"'));

        return string.Equals(tokenBaseName, expectedBaseName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(tokenBaseName, expectedBaseName + ".exe", StringComparison.OrdinalIgnoreCase);
    }

    private static int IndexOfFirstTokenEnd(string commandLine)
    {
        if (commandLine.Length == 0)
        {
            return 0;
        }

        if (commandLine[0] is '"' or '\'')
        {
            char quote = commandLine[0];
            for (int i = 1; i < commandLine.Length; i++)
            {
                if (commandLine[i] == quote)
                {
                    return i + 1;
                }
            }

            return commandLine.Length;
        }

        for (int i = 0; i < commandLine.Length; i++)
        {
            if (char.IsWhiteSpace(commandLine[i]))
            {
                return i;
            }
        }

        return commandLine.Length;
    }
}
