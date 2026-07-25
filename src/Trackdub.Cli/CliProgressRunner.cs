using Spectre.Console;

using Trackdub.Cli.Interactive;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Cli;

/// <summary>
/// Selects CLI progress reporting mode and runs pipeline handlers inside Spectre when appropriate.
/// </summary>
internal static class CliProgressRunner
{
    internal static bool ShouldUseSpectreProgress(
        string progressFormat,
        Func<bool>? isStderrInteractive = null)
    {
        if (!string.Equals(progressFormat, "text", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        Func<bool> isInteractive = isStderrInteractive ?? (() => !Console.IsErrorRedirected);
        return isInteractive();
    }

    internal static async Task<int> ExecuteAsync(
        string progressFormat,
        Func<IProgress<PipelineProgressEvent>?, CancellationToken, Task<int>> execute,
        CancellationToken cancellationToken,
        Func<bool>? isStderrInteractive = null)
    {
        if (ShouldUseSpectreProgress(progressFormat, isStderrInteractive))
        {
            IAnsiConsole console = SpectreStderrConsole.Create();
            return await console.Progress()
                .AutoClear(false)
                .HideCompleted(false)
                .StartAsync(async ctx =>
                {
                    var reporter = new SpectreCliProgressReporter(ctx);
                    return await execute(reporter, cancellationToken).ConfigureAwait(false);
                })
                .ConfigureAwait(false);
        }

        IProgress<PipelineProgressEvent>? progress = string.Equals(
            progressFormat,
            "json",
            StringComparison.OrdinalIgnoreCase)
            ? new CliProgressReporter("json")
            : new CliProgressReporter("text");

        return await execute(progress, cancellationToken).ConfigureAwait(false);
    }
}
