using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Provides retry-with-backoff around ONNX Runtime inference calls for transient
/// ONNX Runtime failures that re-running the <em>same</em> session can actually clear
/// (for example a momentary DirectML scheduling hiccup).
/// </summary>
/// <remarks>
/// This policy re-runs the existing session; it never recreates it. Device-level
/// conditions — memory exhaustion and device removed/lost/hung — survive a bare
/// re-run, so they are deliberately not retried here. They propagate immediately to
/// the device-fallback path (<see cref="DeviceOomExceptionHelper"/> +
/// <c>DeviceFallbackSessionCreator</c>), which can exclude the device and rebuild the
/// session on another one. Retrying them here would only add latency before the same
/// failure resurfaced.
/// </remarks>
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
        // A device that is out of memory or has been removed/lost stays that way for a
        // re-run against the same session, so hand those straight to the device-fallback
        // path instead of burning the backoff budget on them.
        if (DeviceOomExceptionHelper.ClassifyDeviceException(ex) is not null)
        {
            return false;
        }

        // OnnxRuntimeException embeds the error code in its message as "[ErrorCode:XXX]".
        // What is left that a bare re-run can clear: a generic RuntimeException or Fail with
        // no device-level cause. Permanent codes (InvalidArgument, InvalidGraph, etc.) are
        // not retried.
        string message = ex.Message;
        return message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase)
            || message.Contains("[ErrorCode:Fail]", StringComparison.OrdinalIgnoreCase);
    }
}
