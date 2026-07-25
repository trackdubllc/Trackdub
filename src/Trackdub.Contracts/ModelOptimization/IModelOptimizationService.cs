namespace Trackdub.Contracts.ModelOptimization;

public interface IModelOptimizationService
{
    IAsyncEnumerable<string> OptimizeAsync(
        ModelOptimizationRequest request,
        CancellationToken cancellationToken);
}
