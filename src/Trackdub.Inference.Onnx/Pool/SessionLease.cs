using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Pool;

/// <summary>One graph in a multi-graph bundle acquire.</summary>
internal sealed record SessionLeaseRequest(
    SessionPoolKey Key,
    Func<CancellationToken, Task<InferenceSession>> Factory);

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

/// <summary>
/// Keeps a pooled session <em>resident</em> (protected from idle eviction) without holding
/// the exclusive execution gate. Audit §3A: residency ≠ execution exclusivity — an idle
/// reusable model must not require a permanently held <see cref="SessionLease"/>.
/// Take a <see cref="SessionLease"/> only for the duration of a Run() call.
/// </summary>
internal sealed class SessionResidency : IDisposable
{
    private readonly Action unpin;
    private int disposed;

    internal SessionResidency(Action unpin)
    {
        this.unpin = unpin;
    }

    /// <summary>Drops the residency pin. The session stays in the pool until evicted.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
        {
            unpin();
        }
    }
}

/// <summary>
/// All-or-nothing set of exclusive session leases (multi-graph models). Disposing the
/// bundle releases every lease. Never dispose individual leases from a bundle.
/// </summary>
internal sealed class SessionLeaseBundle : IDisposable
{
    private readonly SessionLease[] leases;
    private int disposed;

    internal SessionLeaseBundle(IReadOnlyList<SessionLease> leases)
    {
        this.leases = leases.ToArray();
    }

    public int Count => leases.Length;

    public InferenceSession this[int index] => leases[index].Session;

    public SessionLease LeaseAt(int index) => leases[index];

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        for (int i = leases.Length - 1; i >= 0; i--)
        {
            leases[i].Dispose();
        }
    }
}
