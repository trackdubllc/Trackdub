using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;
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
///
/// A plain "[ErrorCode:RuntimeException]" that doesn't match a known device-level
/// pattern is ambiguous: on DirectML it is usually a momentary scheduling hiccup that
/// a bare re-run clears, but on TensorRT-RTX (CUDA-backed) it can mean the execution
/// context itself is corrupted, in which case re-running the same session just
/// re-throws the same error after burning the backoff budget. Callers that know their
/// session's execution provider should pass it via <c>provider</c> so TensorRT-RTX
/// sessions propagate immediately instead of retrying.
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
    /// <param name="provider">
    /// The execution provider the session was created with, if known. Narrows retry of an
    /// unclassified "[ErrorCode:RuntimeException]" — see remarks on <see cref="InferenceRetryPolicy"/>.
    /// </param>
    public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithRetry(
        this InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind? provider = null)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return session.Run(inputs);
            }
            catch (OnnxRuntimeException ex) when (IsTransient(ex, provider) && ++attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(DefaultDelays[Math.Min(attempt - 1, DefaultDelays.Length - 1)]);
            }
        }
    }

    /// <summary>
    /// Async variant that yields during backoff delays.
    /// </summary>
    /// <param name="provider">
    /// The execution provider the session was created with, if known. Narrows retry of an
    /// unclassified "[ErrorCode:RuntimeException]" — see remarks on <see cref="InferenceRetryPolicy"/>.
    /// </param>
    public static async Task<IDisposableReadOnlyCollection<DisposableNamedOnnxValue>> RunWithRetryAsync(
        this InferenceSession session,
        IReadOnlyCollection<NamedOnnxValue> inputs,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind? provider = null)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return session.Run(inputs);
            }
            catch (OnnxRuntimeException ex) when (IsTransient(ex, provider) && ++attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await Task.Delay(DefaultDelays[Math.Min(attempt - 1, DefaultDelays.Length - 1)], cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// IoBinding variant of <see cref="RunWithRetry"/>, for sessions invoked via
    /// <see cref="InferenceSession.RunWithBindingAndNames"/> instead of plain
    /// <see cref="InferenceSession.Run(IReadOnlyCollection{NamedOnnxValue})"/>. A retry re-runs the
    /// same <paramref name="binding"/> unchanged, so it is safe only when the caller has not yet
    /// consumed/swapped any bound output buffers for this attempt — the same input/output pointers
    /// are simply replayed.
    /// </summary>
    /// <param name="provider">
    /// The execution provider the session was created with, if known. Narrows retry of an
    /// unclassified "[ErrorCode:RuntimeException]" — see remarks on <see cref="InferenceRetryPolicy"/>.
    /// </param>
    public static IDisposableReadOnlyCollection<DisposableNamedOnnxValue> RunWithBindingAndNamesRetry(
        this InferenceSession session,
        RunOptions runOptions,
        OrtIoBinding binding,
        string[] outputNames,
        int maxAttempts = DefaultMaxAttempts,
        CancellationToken cancellationToken = default,
        ExecutionProviderKind? provider = null)
    {
        int attempt = 0;
        while (true)
        {
            try
            {
                return session.RunWithBindingAndNames(runOptions, binding, outputNames);
            }
            catch (OnnxRuntimeException ex) when (IsTransient(ex, provider) && ++attempt < maxAttempts)
            {
                cancellationToken.ThrowIfCancellationRequested();
                Thread.Sleep(DefaultDelays[Math.Min(attempt - 1, DefaultDelays.Length - 1)]);
            }
        }
    }

    private static bool IsTransient(OnnxRuntimeException ex, ExecutionProviderKind? provider) =>
        IsTransientMessage(ex.Message, provider);

    internal static bool IsTransientMessage(string message, ExecutionProviderKind? provider = null)
    {
        if (string.IsNullOrEmpty(message))
        {
            return false;
        }

        // A device that is out of memory or has been removed/lost stays that way for a
        // re-run against the same session, so hand those straight to the device-fallback
        // path instead of burning the backoff budget on them.
        if (DeviceOomExceptionHelper.ClassifyDeviceExceptionMessage(message) is not null)
        {
            return false;
        }

        // [ErrorCode:Fail] is a generic execution failure with no device-level cause; a bare
        // re-run can clear it regardless of execution provider.
        if (message.Contains("[ErrorCode:Fail]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase))
        {
            // Unclassified (non-OOM, non-device-removed) RuntimeException. On TensorRT-RTX this
            // can be a sticky CUDA execution-context error that survives a bare re-run on the same
            // session, so propagate immediately and let the caller's device-fallback path rebuild
            // the session elsewhere instead of burning the backoff budget on a doomed retry.
            return provider != ExecutionProviderKind.TensorRTRtx;
        }

        // Permanent codes (InvalidArgument, InvalidGraph, NotImplemented, etc.) are not retried.
        return false;
    }
}
