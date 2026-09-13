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

    [Fact]
    public void TryEnsureLoadedResult_without_cuda12_in_search_path_reports_missing_runtime()
    {
        string emptyDir = Path.Combine(Path.GetTempPath(), $"trackdub-cuda-empty-{Guid.NewGuid():N}");
        Directory.CreateDirectory(emptyDir);

        try
        {
            TensorRtRtxCudaRuntimeEnsureResult result =
                TensorRtRtxCudaRuntimeBootstrap.TryEnsureLoadedResult([emptyDir]);

            Assert.False(result.Succeeded);
            Assert.Null(result.LoadedPath);
            Assert.Contains("CUDA 12 runtime", result.Detail, StringComparison.Ordinal);
            Assert.Contains("cu12", result.Detail, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (Directory.Exists(emptyDir))
            {
                Directory.Delete(emptyDir, recursive: true);
            }
        }
    }

    [Fact]
    public void DescribeInstalledCudaMajorVersions_is_never_empty()
    {
        string detail = TensorRtRtxCudaRuntimeBootstrap.DescribeInstalledCudaMajorVersions();
        Assert.False(string.IsNullOrWhiteSpace(detail));
    }
}
