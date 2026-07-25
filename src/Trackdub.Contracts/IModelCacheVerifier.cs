using Trackdub.Domain;

namespace Trackdub.Contracts;

public sealed record ModelVerificationResult(
    string ModelId,
    ModelCacheState PreviousState,
    ModelCacheState NewState,
    bool HashMatch,
    string? FailureReason);

/// <summary>
/// Verifies integrity of cached model files against manifest checksums.
/// </summary>
public interface IModelCacheVerifier
{
    Task<ModelVerificationResult> VerifyAsync(string modelId, CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ModelVerificationResult>> VerifyAllAsync(
        IProgress<int>? progress = null,
        CancellationToken cancellationToken = default);
}
