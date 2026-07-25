using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Provides retry-with-backoff around ONNX Runtime inference calls for transient
/// ONNX Runtime failures (GPU memory pressure, driver hiccups on DML/TRT-RTX).
/// </summary>
internal static class InferenceRetryPolicy
{
    private const int DefaultMaxAttempts = 3;
    private static readonly TimeSpan[] DefaultDelays =
    [
        TimeSpan.FromMilliseconds(50),
        TimeSpan.FromMilliseconds(200),
        TimeSpan.FromMilliseconds(800)
    ];

    /// <summary>
    /// Executes an ONNX Runtime inference call with retry on transient <see cref="OnnxRuntimeException"/>.
    /// </summary>
    public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithRetry(
        this InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return session.Run(inputs);
            }
            catch (OnnxRuntimeException ex) when (IsTransient(ex) && ++attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(DefaultDelays[Math.Min(attempt - 1, DefaultDelays.Length - 1)]);
            }
        }
    }

    /// <summary>
    /// Async variant that yields during backoff delays.
    /// </summary>
    public static async Task<IDisposableReadOnlyCollection<DisposableNamedOnnxValue>> RunWithRetryAsync(
        this InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return session.Run(inputs);
            }
            catch (OnnxRuntimeException ex) when (IsTransient(ex) && ++attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(DefaultDelays[Math.Min(attempt - 1, DefaultDelays.Length - 1)], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private static bool IsTransient(OnnxRuntimeException ex)
    {
        // OnnxRuntimeException embeds the error code in its message as "[ErrorCode:XXX]".
        // Transient/recoverable codes: RuntimeException (GPU OOM, driver timeout), Fail (generic).
        // Permanent codes (InvalidArgument, InvalidGraph, etc.) should not be retried.
        string message = ex.Message;
        return message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase)
            || message.Contains("[ErrorCode:Fail]", StringComparison.OrdinalIgnoreCase);
    }
}
