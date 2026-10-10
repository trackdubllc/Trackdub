using System.Runtime.Versioning;
using Microsoft.Windows.AI.MachineLearning;
using Trackdub.Inference.Onnx.WinMlCatalog;

namespace Trackdub.Inference.Onnx.WindowsMl;

[SupportedOSPlatform("windows10.0.19041.0")]
internal static class WindowsMlEnsureReady
{
    private const int MaxAttempts = 3;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(2);

    public static CatalogReadyState MapReadyState(ExecutionProviderReadyState state) =>
        state switch
        {
            ExecutionProviderReadyState.NotPresent => CatalogReadyState.NotPresent,
            ExecutionProviderReadyState.NotReady => CatalogReadyState.NotReady,
            ExecutionProviderReadyState.Ready => CatalogReadyState.Ready,
            _ => throw new ArgumentOutOfRangeException(nameof(state), state, "Unsupported Windows ML ready state."),
        };

    public static CatalogEnsureStatus MapEnsureStatus(ExecutionProviderReadyResultState status) =>
        status switch
        {
            ExecutionProviderReadyResultState.Success => CatalogEnsureStatus.Success,
            ExecutionProviderReadyResultState.InProgress => CatalogEnsureStatus.InProgress,
            ExecutionProviderReadyResultState.Failure => CatalogEnsureStatus.Failure,
            _ => CatalogEnsureStatus.Failure,
        };

    /// <summary>
    /// Awaits preparation, then retries a few times when Windows still reports
    /// <see cref="ExecutionProviderReadyResultState.InProgress"/>. This is a bounded refresh,
    /// not a poll loop.
    /// </summary>
    public static async Task<ExecutionProviderReadyResult> EnsureReadyBoundedAsync(
        ExecutionProvider provider,
        CancellationToken cancellationToken)
    {
        ExecutionProviderReadyResult result = await provider.EnsureReadyAsync()
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
        for (int attempt = 1;
             attempt < MaxAttempts && result.Status == ExecutionProviderReadyResultState.InProgress;
             attempt++)
        {
            await Task.Delay(RetryDelay, cancellationToken).ConfigureAwait(false);
            result = await provider.EnsureReadyAsync().AsTask(cancellationToken).ConfigureAwait(false);
        }

        return result;
    }

    public static string FormatHResult(ExecutionProviderReadyResult result) =>
        result.ExtendedError is null ? "n/a" : $"0x{result.ExtendedError.HResult:X8}";
}
