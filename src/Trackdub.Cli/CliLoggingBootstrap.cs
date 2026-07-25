using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli;

internal static class CliLoggingBootstrap
{
    internal static void EnsureReady(TrackdubSessionFactory factory)
    {
        IAppStoragePaths storagePaths = factory.GetRequiredService<IAppStoragePaths>();
        IApplicationLogger logger = factory.GetRequiredService<IApplicationLogger>();

        string logPath = storagePaths.LogFilePath;
        string? logDirectory = Path.GetDirectoryName(logPath);
        if (!string.IsNullOrWhiteSpace(logDirectory))
        {
            Directory.CreateDirectory(logDirectory);
        }

        if (!File.Exists(logPath))
        {
            using FileStream _ = File.Open(
                logPath,
                FileMode.OpenOrCreate,
                FileAccess.ReadWrite,
                FileShare.ReadWrite);
        }

        logger.LogInformation("Trackdub CLI started.");
    }
}
