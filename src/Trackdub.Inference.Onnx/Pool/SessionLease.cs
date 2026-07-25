using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>
/// Scoped, exclusive handle to a pooled <see cref="InferenceSession"/>.
/// Disposing this lease releases the session back to the <see cref="InferenceSessionPool"/>
/// so another caller can acquire it. Never dispose the underlying <see cref="SessionLease.Session"/>
/// directly — always dispose the lease instead.
/// </summary>
internal sealed class SessionLease : IDisposable
{
    private readonly Action release;
    private int disposed;

    internal SessionLease(InferenceSession session, Action release)
    {
        Session = session;
        this.release = release;
    }

    /// <summary>The pooled session.  Valid only while the lease is held (before <see cref="Dispose"/>).</summary>
    public InferenceSession Session { get; }

    /// <summary>Releases the session back to the pool.  Safe to call more than once.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            release();
        }
    }
}
