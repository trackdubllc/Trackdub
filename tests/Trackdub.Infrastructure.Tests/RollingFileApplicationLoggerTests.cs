using Trackdub.Infrastructure.Logging;

namespace Trackdub.Infrastructure.Tests;

public sealed class RollingFileApplicationLoggerTests : IDisposable
{
    private readonly List<string> tempDirectories = [];

    [Fact]
    public void Flush_waits_for_queued_entries_to_reach_disk()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 64 * 1024,
            maxArchiveFiles: 2,
            minimumLevel: ApplicationLogLevel.Warning);

        for (int i = 0; i < 50; i++)
        {
            logger.LogWarning($"flush-barrier-{i}");
        }

        logger.Flush(TimeSpan.FromSeconds(2));

        string log = File.ReadAllText(logPath);
        Assert.Contains("flush-barrier-0", log);
        Assert.Contains("flush-barrier-49", log);
    }

    [Fact]
    public void Flush_returns_immediately_when_already_settled_including_zero_timeout()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 64 * 1024,
            maxArchiveFiles: 2,
            minimumLevel: ApplicationLogLevel.Warning);

        logger.LogWarning("already-settled");
        logger.Flush(TimeSpan.FromSeconds(2));

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        logger.Flush(TimeSpan.Zero);
        stopwatch.Stop();

        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromMilliseconds(200),
            $"Expected near-instant Flush(TimeSpan.Zero) after settle, elapsed={stopwatch.Elapsed.TotalMilliseconds:F1}ms.");
        Assert.Contains("already-settled", File.ReadAllText(logPath));
    }

    [Fact]
    public void LogErrorSynchronously_throws_when_write_fails_so_crash_fallback_can_run()
    {
        string directory = CreateTempDirectory();
        string blockerPath = Path.Combine(directory, "not-a-directory");
        File.WriteAllText(blockerPath, "blocker");
        string logPath = Path.Combine(blockerPath, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 64 * 1024,
            maxArchiveFiles: 2,
            minimumLevel: ApplicationLogLevel.Warning);

        IOException ex = Assert.Throws<IOException>(
            () => logger.LogErrorSynchronously("crash entry", new InvalidOperationException("boom")));

        Assert.Contains(logPath, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Flush_includes_entries_admitted_just_before_snapshot()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 64 * 1024,
            maxArchiveFiles: 2,
            minimumLevel: ApplicationLogLevel.Warning);

        const int writers = 8;
        const int entriesPerWriter = 25;
        var barrier = new Barrier(writers + 1);
        var tasks = new Task[writers];
        for (int writerIndex = 0; writerIndex < writers; writerIndex++)
        {
            int captured = writerIndex;
            tasks[writerIndex] = Task.Run(() =>
            {
                barrier.SignalAndWait();
                for (int i = 0; i < entriesPerWriter; i++)
                {
                    logger.LogWarning($"concurrent-{captured}-{i}");
                }
            });
        }

        barrier.SignalAndWait();
        await Task.WhenAll(tasks);
        logger.Flush(TimeSpan.FromSeconds(5));

        string log = File.ReadAllText(logPath);
        for (int writerIndex = 0; writerIndex < writers; writerIndex++)
        {
            for (int i = 0; i < entriesPerWriter; i++)
            {
                Assert.Contains($"concurrent-{writerIndex}-{i}", log);
            }
        }
    }

    [Fact]
    public void Writes_verbose_entries_to_configured_log_file()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "Trackdub", "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 4096,
            maxArchiveFiles: 2,
            minimumLevel: ApplicationLogLevel.Debug);

        logger.LogDebug("debug detail");
        logger.LogInformation("information detail");
        logger.LogWarning("warning detail");
        logger.LogError("error detail", new InvalidOperationException("sample failure"));
        logger.Dispose();

        string log = File.ReadAllText(logPath);
        Assert.Contains("[DEBUG] [pid:", log);
        Assert.Contains("debug detail", log);
        Assert.Contains("[INFO] [pid:", log);
        Assert.Contains("information detail", log);
        Assert.Contains("[WARN] [pid:", log);
        Assert.Contains("warning detail", log);
        Assert.Contains("[ERROR] [pid:", log);
        Assert.Contains("error detail", log);
        Assert.Contains("sample failure", log);
    }

    [Fact]
    public void Rotates_log_file_when_size_cap_is_reached()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 512,
            maxArchiveFiles: 2,
            maxEntryCharacters: 1024);

        for (int entryIndex = 0; entryIndex < 20; entryIndex++)
        {
            logger.LogWarning($"entry {entryIndex:D2} {new string('x', 120)}");
        }
        logger.Dispose();

        string firstArchivePath = Path.Combine(directory, "trackdub.1.log");
        string secondArchivePath = Path.Combine(directory, "trackdub.2.log");
        string thirdArchivePath = Path.Combine(directory, "trackdub.3.log");

        Assert.True(File.Exists(logPath));
        Assert.True(File.Exists(firstArchivePath));
        Assert.True(File.Exists(secondArchivePath));
        Assert.False(File.Exists(thirdArchivePath));
        Assert.True(new FileInfo(logPath).Length <= 1024);
    }

    [Fact]
    public void Uses_warning_minimum_level_by_default()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(logPath);

        logger.LogDebug("debug detail");
        logger.LogInformation("information detail");
        logger.LogWarning("warning detail");
        logger.LogError("error detail");
        logger.Dispose();

        string log = File.ReadAllText(logPath);
        Assert.DoesNotContain("debug detail", log, StringComparison.Ordinal);
        Assert.DoesNotContain("information detail", log, StringComparison.Ordinal);
        Assert.Contains("warning detail", log, StringComparison.Ordinal);
        Assert.Contains("error detail", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Suppresses_entries_below_configured_minimum_level()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 4096,
            maxArchiveFiles: 2,
            minimumLevel: ApplicationLogLevel.Warning);

        logger.LogDebug("debug detail");
        logger.LogInformation("information detail");
        logger.LogWarning("warning detail");
        logger.LogError("error detail");
        logger.Dispose();

        string log = File.ReadAllText(logPath);
        Assert.DoesNotContain("debug detail", log, StringComparison.Ordinal);
        Assert.DoesNotContain("information detail", log, StringComparison.Ordinal);
        Assert.Contains("warning detail", log, StringComparison.Ordinal);
        Assert.Contains("error detail", log, StringComparison.Ordinal);
    }

    [Fact]
    public void Prunes_archives_above_configured_archive_limit()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        string firstArchivePath = Path.Combine(directory, "trackdub.1.log");
        string secondArchivePath = Path.Combine(directory, "trackdub.2.log");
        string staleArchivePath = Path.Combine(directory, "trackdub.3.log");
        File.WriteAllText(firstArchivePath, "first");
        File.WriteAllText(secondArchivePath, "second");
        File.WriteAllText(staleArchivePath, "stale");
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 4096,
            maxArchiveFiles: 2);

        logger.LogWarning("warning detail");
        logger.Dispose();

        Assert.True(File.Exists(firstArchivePath));
        Assert.True(File.Exists(secondArchivePath));
        Assert.False(File.Exists(staleArchivePath));
    }

    [Fact]
    public void RotateOnStartup_archives_existing_log_as_session_1()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");
        string archivePath = Path.Combine(directory, "trackdub.1.log");

        File.WriteAllText(logPath, "previous session content");

        using (new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 4096,
            maxArchiveFiles: 10,
            rotateOnStartup: true))
        {
        }

        // The previous session's log should now be trackdub.1.log.
        Assert.True(File.Exists(archivePath));
        Assert.Equal("previous session content", File.ReadAllText(archivePath));
    }

    [Fact]
    public void RotateOnStartup_shifts_archives_and_prunes_beyond_limit()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");

        // Create 10 existing archive files (the max we keep).
        File.WriteAllText(logPath, "session 0");
        for (int i = 1; i <= 10; i++)
        {
            File.WriteAllText(Path.Combine(directory, $"trackdub.{i}.log"), $"session {i}");
        }

        using (new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 4096,
            maxArchiveFiles: 10,
            rotateOnStartup: true))
        {
        }

        // The old "session 0" log is now archive 1.
        Assert.Equal("session 0", File.ReadAllText(Path.Combine(directory, "trackdub.1.log")));
        for (int i = 2; i <= 10; i++)
        {
            Assert.Equal($"session {i - 1}", File.ReadAllText(Path.Combine(directory, $"trackdub.{i}.log")));
        }
        Assert.False(File.Exists(Path.Combine(directory, "trackdub.11.log")));
        Assert.DoesNotContain("session 10", Directory.EnumerateFiles(directory, "trackdub.*.log")
            .Select(File.ReadAllText));
    }

    [Fact]
    public void RotateOnStartup_does_not_fail_when_no_existing_log()
    {
        string directory = CreateTempDirectory();
        string logPath = Path.Combine(directory, "trackdub.log");

        // No existing log file.
        using var logger = new RollingFileApplicationLogger(
            logPath,
            maxFileBytes: 4096,
            maxArchiveFiles: 10,
            rotateOnStartup: true);

        logger.LogWarning("first entry after startup rotation");
        logger.Dispose();

        // New log should have been created.
        Assert.True(File.Exists(logPath));
        Assert.Contains("first entry after startup rotation", File.ReadAllText(logPath));
    }

    public void Dispose()
    {
        foreach (string tempDirectory in tempDirectories)
        {
            try
            {
                if (Directory.Exists(tempDirectory))
                {
                    Directory.Delete(tempDirectory, recursive: true);
                }
            }
            catch
            {
                // Best-effort cleanup for temp log folders created by tests.
            }
        }
    }

    private string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), "Trackdub.Infrastructure.Tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        tempDirectories.Add(path);
        return path;
    }
}
