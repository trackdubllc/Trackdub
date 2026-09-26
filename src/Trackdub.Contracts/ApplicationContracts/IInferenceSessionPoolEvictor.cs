namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>
/// Evicts idle ONNX inference sessions from the shared pool (for example after hardware policy changes).
/// </summary>
public interface IInferenceSessionPoolEvictor
{
    Task EvictAllIdleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Evicts least-recently-used idle sessions until the pool's estimated resident VRAM for
    /// idle sessions is at or below <paramref name="targetVramMb"/>. Returns the number of
    /// sessions evicted. Leased sessions are never evicted.
    /// </summary>
    Task<int> TrimToVramBudgetAsync(long targetVramMb, CancellationToken cancellationToken = default);
}
