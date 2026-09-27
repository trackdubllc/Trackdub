using Trackdub.Contracts.ApplicationContracts;

namespace Trackdub.Inference.Onnx.Pool;

public sealed class InferenceSessionPoolEvictor : IInferenceSessionPoolEvictor
{
    public Task EvictAllIdleAsync(CancellationToken cancellationToken = default)
        => InferenceSessionPool.Shared.EvictAllIdleAsync(cancellationToken);

    public Task<int> TrimToVramBudgetAsync(long targetVramMb, CancellationToken cancellationToken = default)
        => InferenceSessionPool.Shared.TrimToVramBudgetAsync(targetVramMb, cancellationToken);
}
