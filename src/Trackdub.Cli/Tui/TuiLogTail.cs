namespace Trackdub.Cli.Tui;

internal static class TuiLogTail
{
    internal const int DefaultLineCount = 40;

    internal static IReadOnlyList<string> ReadLastLines(string logFilePath, int lineCount = DefaultLineCount)
    {
        if (lineCount <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(lineCount));
        }

        if (!File.Exists(logFilePath))
        {
            return
            [
                "(No log file yet.)",
                "Trackdub writes to this path when the CLI or desktop app runs.",
                logFilePath,
            ];
        }

        try
        {
            using var stream = new FileStream(
                logFilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite);
            using var reader = new StreamReader(stream);
            var lines = new List<string>();
            while (reader.ReadLine() is { } line)
            {
                lines.Add(line);
            }

            if (lines.Count == 0)
            {
                return ["(log file is empty)"];
            }

            return lines.Count <= lineCount
                ? lines
                : lines[^lineCount..];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [$"Could not read log file: {ex.Message}"];
        }
    }
}
