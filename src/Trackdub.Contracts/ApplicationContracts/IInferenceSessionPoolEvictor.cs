namespace Trackdub.Contracts.ApplicationContracts;

/// <summary>
/// Evicts idle ONNX inference sessions from the shared pool (for example after hardware policy changes).
/// </summary>
public interface IInferenceSessionPoolEvictor
{
    Task EvictAllIdleAsync(CancellationToken cancellationToken = default);
}
