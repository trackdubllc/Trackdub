using System.Net;

namespace Trackdub.Infrastructure.Retry;

/// <summary>
/// Pre-built retry policies for common Infrastructure operations.
/// </summary>
public readonly record struct RetryPolicy
{
    /// <summary>File system commit retry: 6 attempts with 50ms–500ms cascade.</summary>
    public static readonly RetryPolicy FileSystem = new()
    {
        MaxAttempts = 6,
        DelaysMs = [50, 100, 200, 350, 500]
    };

    /// <summary>Download retry: 4 attempts with 250ms–3s cascade.</summary>
    public static readonly RetryPolicy Download = new()
    {
        MaxAttempts = 4,
        DelaysMs = [250, 1000, 3000]
    };

    /// <summary>Maximum number of retry attempts (inclusive of the first try).</summary>
    public required int MaxAttempts { get; init; }

    /// <summary>Millisecond delays before retries. Length must be MaxAttempts - 1.</summary>
    public required int[] DelaysMs { get; init; }

    /// <summary>Returns the delay for the given attempt (1-based). Clamped to last delay.</summary>
    public TimeSpan GetDelay(int attempt) =>
        TimeSpan.FromMilliseconds(DelaysMs[Math.Min(attempt - 1, DelaysMs.Length - 1)]);
}

/// <summary>
/// Executes retry loops for exception-based failure scenarios.
/// </summary>
public static class RetryHelper
{
    /// <summary>
    /// Executes <paramref name="operation"/> with retries. <paramref name="isTransient"/> controls
    /// which exceptions trigger a retry. All other exceptions propagate immediately.
    /// </summary>
    public static async Task<T> ExecuteAsync<T>(
        Func<int, Task<T>> operation,
        RetryPolicy policy,
        Func<Exception, bool> isTransient,
        Action<int, Exception>? onRetry = null,
        CancellationToken cancellationToken = default)
    {
        for (int attempt = 1; attempt <= policy.MaxAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return await operation(attempt).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex) when (isTransient(ex) && attempt < policy.MaxAttempts)
            {
                onRetry?.Invoke(attempt, ex);
                TimeSpan delay = policy.GetDelay(attempt);
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
            }
        }

        // Should never reach here — retries exit via return or rethrow.
        throw new InvalidOperationException("Retry loop exhausted without completing or throwing.");
    }
}

/// <summary>
/// Shared retry helpers for HTTP download scenarios.
/// </summary>
public static class DownloadRetry
{
    /// <summary>Status codes that warrant a retry (transient server/connectivity failures).</summary>
    public static bool ShouldRetryStatus(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout or
            HttpStatusCode.TooManyRequests ||
        (int)statusCode >= 500;

    /// <summary>Exception types that warrant a download retry.</summary>
    public static bool IsTransientException(Exception exception, CancellationToken cancellationToken = default) =>
        exception is IOException or HttpRequestException ||
        exception.InnerException is IOException or HttpRequestException ||
        IsClientTimeoutException(exception, cancellationToken);

    /// <summary>
    /// HttpClient.Timeout and similar client-side timeouts surface as
    /// <see cref="OperationCanceledException"/> without the caller token being cancelled.
    /// </summary>
    public static bool IsClientTimeoutException(Exception exception, CancellationToken cancellationToken) =>
        exception is OperationCanceledException && !cancellationToken.IsCancellationRequested;
}
