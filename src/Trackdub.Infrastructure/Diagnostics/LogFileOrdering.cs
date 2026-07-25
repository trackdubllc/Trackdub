namespace Trackdub.Infrastructure.Diagnostics;

internal static class LogFileOrdering
{
    public static IOrderedEnumerable<string> OrderByRotation(IEnumerable<string> paths, string activeLogPath)
    {
        string logFileName = Path.GetFileNameWithoutExtension(activeLogPath);
        string logExtension = Path.GetExtension(activeLogPath);

        return paths
            .OrderBy(path => IsActiveLogPath(path, activeLogPath) ? 1 : 0)
            .ThenBy(path => TryGetArchiveIndex(path, logFileName, logExtension) ?? int.MaxValue)
            .ThenBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
            .ThenBy(static path => path, StringComparer.OrdinalIgnoreCase);
    }

    private static bool IsActiveLogPath(string path, string activeLogPath) =>
        string.Equals(
            GetComparablePath(path),
            GetComparablePath(activeLogPath),
            StringComparison.OrdinalIgnoreCase);

    private static int? TryGetArchiveIndex(string path, string logFileName, string logExtension)
    {
        string fileName = Path.GetFileName(path);
        string prefix = logFileName + ".";
        if (!fileName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
            !fileName.EndsWith(logExtension, StringComparison.OrdinalIgnoreCase) ||
            fileName.Length <= prefix.Length + logExtension.Length)
        {
            return null;
        }

        string indexText = fileName[prefix.Length..^logExtension.Length];
        return int.TryParse(indexText, out int index) ? index : null;
    }

    private static string GetComparablePath(string path)
    {
        try
        {
            return Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or IOException or NotSupportedException)
        {
            return path;
        }
    }
}
