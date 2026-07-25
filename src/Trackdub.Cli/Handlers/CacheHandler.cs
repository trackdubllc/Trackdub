using System.Text.Json;

using Trackdub.Contracts;
using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

internal static class CacheHandler
{
    public static async Task<int> ClearEnginesAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        IEngineCacheMaintenanceService maintenance = factory.GetRequiredService<IEngineCacheMaintenanceService>();
        EngineCacheClearResult result = maintenance.Clear();

        var payload = new
        {
            command = "cache clear engines",
            cacheDirectory = result.CacheDirectory,
            directoryExisted = result.DirectoryExisted,
            filesRemoved = result.FilesRemoved,
            filesSkipped = result.FilesSkipped,
            bytesFreed = result.BytesFreed,
            message = BuildClearEnginesMessage(result),
        };

        string json = JsonSerializer.Serialize(payload, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    private static string BuildClearEnginesMessage(EngineCacheClearResult result)
    {
        if (!result.DirectoryExisted)
        {
            return "Engine cache directory does not exist; nothing to clear.";
        }

        if (result.FilesRemoved == 0 && result.FilesSkipped == 0)
        {
            return "Engine cache directory exists but contained no files.";
        }

        string summary = result.FilesRemoved > 0
            ? $"Removed {result.FilesRemoved} engine cache file(s); freed {result.BytesFreed} bytes."
            : "No engine cache files could be removed.";

        if (result.FilesSkipped > 0)
        {
            summary += $" Skipped {result.FilesSkipped} file(s) due to access restrictions.";
        }

        return summary;
    }
}
