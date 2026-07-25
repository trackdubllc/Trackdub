namespace Trackdub.Application.Transcripts;

public static class StemSeparationTempDirectories
{
    private const string DirectoryPrefix = "trackdub-stems-";
    private static readonly TimeSpan StaleDirectoryAge = TimeSpan.FromHours(24);

    public static string GetRunDirectory(Guid stageRunId)
    {
        if (stageRunId == Guid.Empty)
        {
            throw new ArgumentException("Stage run id is required.", nameof(stageRunId));
        }

        return Path.Combine(Path.GetTempPath(), $"{DirectoryPrefix}{stageRunId:N}");
    }

    public static void CleanupStale(DateTimeOffset utcNow)
    {
        string tempRoot = Path.GetTempPath();
        string[] candidates;
        try
        {
            candidates = Directory.GetDirectories(tempRoot, $"{DirectoryPrefix}*");
        }
        catch
        {
            return;
        }

        DateTime cutoffUtc = utcNow.UtcDateTime - StaleDirectoryAge;
        foreach (string candidate in candidates)
        {
            DeleteIfStale(candidate, cutoffUtc);
        }
    }

    public static void DeleteIfExists(string directory)
    {
        try
        {
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup only.
        }
    }

    private static void DeleteIfStale(string directory, DateTime cutoffUtc)
    {
        try
        {
            var info = new DirectoryInfo(directory);
            if (!info.Exists || info.LastWriteTimeUtc > cutoffUtc)
            {
                return;
            }

            info.Delete(recursive: true);
        }
        catch
        {
            // Startup-safe cleanup must never block a new stem run.
        }
    }
}
