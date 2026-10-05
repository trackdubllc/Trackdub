using Trackdub.Domain;

namespace Trackdub.Domain.Tests;

public sealed class NvidiaGpuArchitectureClassifierTests
{
    [Theory]
    [InlineData("NVIDIA GeForce RTX 5090", NvidiaGpuArchitectureBucket.Blackwell)]
    [InlineData("RTX 5080 Blackwell", NvidiaGpuArchitectureBucket.Blackwell)]
    [InlineData("NVIDIA GeForce RTX 4090", NvidiaGpuArchitectureBucket.Ada)]
    [InlineData("NVIDIA RTX A30", NvidiaGpuArchitectureBucket.Ampere)]
    [InlineData("NVIDIA GeForce GTX 1660", NvidiaGpuArchitectureBucket.Turing)]
    [InlineData("NVIDIA RTX A2000", NvidiaGpuArchitectureBucket.Ampere)]
    [InlineData("NVIDIA RTX A4000", NvidiaGpuArchitectureBucket.Ampere)]
    [InlineData("NVIDIA RTX A6000", NvidiaGpuArchitectureBucket.Ampere)]
    [InlineData("NVIDIA A100-SXM4-40GB", NvidiaGpuArchitectureBucket.Ampere)]
    [InlineData("NVIDIA A40", NvidiaGpuArchitectureBucket.Ampere)]
    [InlineData("NVIDIA L40S", NvidiaGpuArchitectureBucket.Ada)]
    [InlineData("NVIDIA RTX 4000 Ada Generation", NvidiaGpuArchitectureBucket.Ada)]
    public void ClassifyFromName_MapsConsumerGpuNames(string gpuName, NvidiaGpuArchitectureBucket expected) =>
        Assert.Equal(expected, NvidiaGpuArchitectureClassifier.ClassifyFromName(gpuName));

    [Fact]
    public void ToAfxArchitectureBucket_MapsBlackwell() =>
        Assert.Equal("blackwell", NvidiaGpuArchitectureClassifier.ToAfxArchitectureBucket(NvidiaGpuArchitectureBucket.Blackwell));
}
