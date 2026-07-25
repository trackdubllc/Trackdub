using System.Text.Json;

using Trackdub.Sdk;

namespace Trackdub.Cli.Handlers;

internal static class ConfigHandler
{
    public static async Task<int> PathsAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        TrackdubConfigPathSnapshot snapshot = TrackdubConfig.CapturePaths(factory);
        string json = JsonSerializer.Serialize(snapshot, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }

    public static async Task<int> ShowAsync(
        TrackdubSessionFactory factory,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        TrackdubConfigShowSnapshot snapshot = await TrackdubConfig
            .CaptureShowAsync(factory, cancellationToken)
            .ConfigureAwait(false);

        string json = JsonSerializer.Serialize(snapshot, CliJsonOptions.Default);
        await output.WriteLineAsync(json).ConfigureAwait(false);
        return Program.ExitSuccess;
    }
}
