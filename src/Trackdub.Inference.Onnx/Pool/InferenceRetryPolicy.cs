using Microsoft.ML.OnnxRuntime;
using Trackdub.Contracts.Pipeline;
using Trackdub.Domain;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Provides retry-with-backoff around ONNX Runtime inference calls for transient
/// ONNX Runtime failures that re-running the <em>same</em> session can actually clear
/// (for example a momentary DirectML scheduling hiccup).
/// </summary>
/// <remarks>
/// This policy re-runs the existing session; it never recreates it, and nothing in the
/// inference path currently catches a run failure to rebuild the session on another device
/// (<c>DeviceFallbackSessionCreator</c> only covers session creation). Failures therefore
/// propagate to the caller and fail the stage. Device failures — device removed/lost/hung and
/// sticky CUDA errors, as classified by <see cref="DeviceOomExceptionHelper"/> — can never be
/// cleared by a re-run, so they are not retried. Memory exhaustion can clear as other work frees
/// memory, so it is retried like any other transient failure.
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

        switch (DeviceOomExceptionHelper.ClassifyDeviceExceptionMessage(message))
        {
            // Device removed/lost/hung, or a sticky CUDA error (illegal address, launch failure,
            // ...) that leaves the process's CUDA state unusable: a re-run can never succeed.
            case DeviceDegradationKind.DeviceFailed:
                return false;

            // Memory pressure can clear once concurrent work releases its allocations, and a CUDA
            // out-of-memory error is not sticky. Nothing re-plans inference-time failures onto
            // another device yet, so dropping these retries would only fail the stage sooner.
            case DeviceDegradationKind.MemoryExhausted:
                return true;
        }

        // [ErrorCode:Fail] is a generic execution failure with no device-level cause; a bare
        // re-run can clear it regardless of execution provider.
        if (message.Contains("[ErrorCode:Fail]", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (message.Contains("[ErrorCode:RuntimeException]", StringComparison.OrdinalIgnoreCase))
        {
            // Unclassified (non-OOM, non-device-failure) RuntimeException. On TensorRT-RTX this
            // can be a CUDA execution-context error that survives a bare re-run on the same
            // session, so propagate it to the caller immediately instead of burning the backoff
            // budget on a doomed retry.
            return provider != ExecutionProviderKind.TensorRTRtx;
        }

        // Permanent codes (InvalidArgument, InvalidGraph, NotImplemented, etc.) are not retried.
        return false;
    }
}
