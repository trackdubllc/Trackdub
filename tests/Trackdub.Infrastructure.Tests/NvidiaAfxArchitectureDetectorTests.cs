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
    public void BucketsFromAdapterNames_PutsTheNewestArchitectureFirst_AndKeepsStaleOnesAsFallbacks()
    {
        // A GPU swap can leave the old adapter's registry entry behind, listed first.
        string[] buckets = [.. NvidiaAfxArchitectureDetector.BucketsFromAdapterNames(
            ["NVIDIA GeForce RTX 3080", "Intel(R) UHD Graphics 770", "NVIDIA GeForce RTX 5070"])];

        Assert.Equal(["blackwell", "ampere"], buckets);
    }

    [Fact]
    public void BucketsFromAdapterNames_DeduplicatesTheSameArchitecture()
    {
        Assert.Equal(
            ["ada"],
            [.. NvidiaAfxArchitectureDetector.BucketsFromAdapterNames(
                ["NVIDIA GeForce RTX 4090", "NVIDIA GeForce RTX 4070"])]);
    }

    [Theory]
    [InlineData("NVIDIA GeForce GTX 1080")]
    [InlineData("NVIDIA GeForce GTX 1660 SUPER")]
    public void BucketsFromAdapterNames_ExcludesGtxCards_BecauseTheyHaveNoTensorCores(string adapter)
    {
        Assert.Empty(NvidiaAfxArchitectureDetector.BucketsFromAdapterNames([adapter]));
        Assert.Equal("unsupported", NvidiaAfxArchitectureDetector.DetectFromAdapterNames([adapter]));
    }

    [Theory]
    [InlineData("NVIDIA RTX A4000", "ampere")]
    [InlineData("NVIDIA RTX A6000", "ampere")]
    [InlineData("NVIDIA A100-SXM4-40GB", "ampere")]
    [InlineData("NVIDIA RTX 4000 Ada Generation", "ada")]
    public void DetectFromAdapterNames_ClassifiesWorkstationAndDataCenterCards(string adapter, string expected) =>
        Assert.Equal(expected, NvidiaAfxArchitectureDetector.DetectFromAdapterNames([adapter]));

    [Fact]
    public void DecodeAdapterString_ReadsTheUtf16NameTheDriverStores()
    {
        byte[] stored = System.Text.Encoding.Unicode.GetBytes("NVIDIA GeForce RTX 5070\0");

        Assert.Equal("NVIDIA GeForce RTX 5070", NvidiaAfxArchitectureDetector.DecodeAdapterString(stored));
    }

    [Fact]
    public void DetectArchitectureBuckets_UsesTheOverrideAsTheOnlyCandidate()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string? previous = Environment.GetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME");
        try
        {
            Environment.SetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME", "NVIDIA GeForce RTX 4090");

            Assert.Equal(["ada"], [.. new NvidiaAfxArchitectureDetector().DetectArchitectureBuckets()]);
        }
        finally
        {
            Environment.SetEnvironmentVariable("TRACKDUB_NVIDIA_GPU_NAME", previous);
        }
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
