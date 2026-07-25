using Trackdub.Infrastructure.Components.NvidiaAfx;

namespace Trackdub.Infrastructure.Tests;

public sealed class NvidiaAfxArchitectureDetectorTests
{
    [Theory]
    [InlineData("RTX 2080")]
    [InlineData("partial override typo")]
    public void DetectOverrideArchitectureBucket_WhenOverrideIsUnrecognized_ReturnsTuring(string gpuName) =>
        Assert.Equal("turing", NvidiaAfxArchitectureDetector.DetectOverrideArchitectureBucket(gpuName));

    [Fact]
    public void DetectArchitectureBucket_WhenGpuNameUnset_ReturnsTuringOnWindows()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? previous = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME");
        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME", null);

            var detector = new NvidiaAfxArchitectureDetector();

            Assert.Equal("turing", detector.DetectArchitectureBucket());
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME", previous);
        }
    }
}
