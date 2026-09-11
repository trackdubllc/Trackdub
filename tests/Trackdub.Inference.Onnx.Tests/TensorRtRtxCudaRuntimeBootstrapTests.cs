using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class TensorRtRtxCudaRuntimeBootstrapTests
{
    [Fact]
    public void DiscoverSearchDirectories_IncludesConfiguredDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), $"trackdub-cuda-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        string? previous = Environment.GetEnvironmentVariable("TRACKDUB_CUDA12_BIN_DIR");

        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_CUDA12_BIN_DIR", directory);
            Assert.Contains(
                directory,
                TensorRtRtxCudaRuntimeBootstrap.DiscoverSearchDirectories(),
                StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_CUDA12_BIN_DIR", previous);
            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
    }
}
