using Trackdub.Contracts;

namespace Trackdub.Application.Transcripts;

/// <summary>
/// Headless (CLI/SDK) callbacks that auto-download missing models without UI prompts.
/// </summary>
public static class HeadlessRuntimeModelSetup
{
    public static RuntimeModelSetupCallbacks CreateCallbacks(
        CancellationToken cancellationToken,
        IProgress<ModelDownloadProgress>? progress = null,
        TextWriter? statusWriter = null)
    {
        IProgress<ModelDownloadProgress> effectiveProgress = progress ?? NullProgress.Instance;
        TextWriter writer = statusWriter ?? Console.Error;

        return new RuntimeModelSetupCallbacks(
            ResolveDecisionAsync: prompt => Task.FromResult(
                prompt.Status.CanAutoDownload
                    ? RuntimeModelSetupDecision.Download
                    : RuntimeModelSetupDecision.Cancel),
            PickImportFileAsync: () => Task.FromResult<string?>(null),
            CreateDownloadProgress: _ => effectiveProgress,
            RunOperationAsync: async (operation, busyMessage) =>
            {
                if (!string.IsNullOrWhiteSpace(busyMessage))
                {
                    await writer.WriteLineAsync(busyMessage).ConfigureAwait(false);
                }

                await operation(cancellationToken).ConfigureAwait(false);
            });
    }

    private sealed class NullProgress : IProgress<ModelDownloadProgress>
    {
        public static NullProgress Instance { get; } = new();

        public void Report(ModelDownloadProgress value)
        {
        }
    }
}
