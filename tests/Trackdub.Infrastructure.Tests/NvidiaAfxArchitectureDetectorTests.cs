using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Infrastructure.Tests;

public sealed class NvidiaAfxArchitectureDetectorTests
{
    [Theory]
    [InlineData("RTX 2080")]
    [InlineData("partial override typo")]
    public void DetectOverrideArchitectureBucket_WhenOverrideIsUnrecognized_ReturnsTuring(string gpuName) =>
        Assert.Equal("turing", NvidiaAfxArchitectureDetector.DetectOverrideArchitectureBucket(gpuName));

    [Theory]
    [InlineData("NVIDIA GeForce RTX 5070", "blackwell")]
    [InlineData("NVIDIA GeForce RTX 4090", "ada")]
    [InlineData("NVIDIA GeForce RTX 3080", "ampere")]
    [InlineData("NVIDIA GeForce RTX 2080", "turing")]
    public void DetectFromAdapterNames_ClassifiesTheNvidiaAdapter(string nvidiaAdapter, string expected) =>
        Assert.Equal(
            expected,
            NvidiaAfxArchitectureDetector.DetectFromAdapterNames(["Intel(R) UHD Graphics 770", nvidiaAdapter]));

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770")]
    [InlineData("Microsoft Basic Display Adapter")]
    public void DetectFromAdapterNames_WithoutNvidiaAdapter_ReturnsUnsupported(string adapter)
    {
        Assert.Equal("unsupported", NvidiaAfxArchitectureDetector.DetectFromAdapterNames([adapter]));
        Assert.Equal("unsupported", NvidiaAfxArchitectureDetector.DetectFromAdapterNames([]));
    }

    [Fact]
    public void DetectArchitectureBucket_WhenGpuNameOverrideSet_UsesTheOverride()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? previous = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME");
        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME", "NVIDIA GeForce RTX 4090");

            Assert.Equal("ada", new NvidiaAfxArchitectureDetector().DetectArchitectureBucket());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME", previous);
        }
    }
}
