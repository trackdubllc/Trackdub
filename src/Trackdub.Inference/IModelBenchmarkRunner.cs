using Trackdub.Domain;

namespace Trackdub.Inference;

public interface IModelBenchmarkRunner
{
    Task<BenchmarkReport> RunAsync(BenchmarkRequest request, CancellationToken cancellationToken);
}
