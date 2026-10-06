using Trackdub.Contracts.Benchmarking;
using Trackdub.Inference.Onnx.Pool;
using Trackdub.Sdk;

namespace Trackdub.Sdk.Tests;

/// <summary>
/// The SDK builds the headless composition directly in <see cref="TrackdubBuilder.Build"/>
/// (not through <see cref="Trackdub.Composition.Headless.HeadlessDubbingHost"/>), so it must
/// hand the container's process-GPU reader to the shared ONNX session pool itself — otherwise
/// the advertised default-on accelerator admission silently stays reservation-only on the SDK
/// path. Disposing the factory releases that process-wide binding.
/// </summary>
public sealed class TrackdubBuilderProcessGpuAdmissionTests
{
    [Fact]
    public void Build_binds_process_gpu_reader_to_shared_pool_admission_and_dispose_releases_it()
    {
        IProcessGpuMemoryReader? previous = SharedPoolOptions.ProcessGpuMemoryReader;
        var factory = new TrackdubBuilder().Build();
        try
        {
#if WINDOWS
            Assert.NotNull(SharedPoolOptions.ProcessGpuMemoryReader);
            factory.Dispose();
            Assert.Null(SharedPoolOptions.ProcessGpuMemoryReader);
#else
            // No platform reader here: the pool keeps its reservation-only model.
            Assert.Null(SharedPoolOptions.ProcessGpuMemoryReader);
            factory.Dispose();
#endif
        }
        finally
        {
            factory.Dispose();
            SharedPoolOptions.UseProcessGpuMemoryReader(previous);
        }
    }
}
