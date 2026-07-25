using Trackdub.Composition.HardwareProfiler;
using Trackdub.Domain;
using Xunit;

namespace Trackdub.Composition.Tests.HardwareProfiler;

public sealed class HardwareProfilerBenchmarkProviderTests
{
    [Fact]
    public void ResolveProfilerBenchmarkProviderPreference_NvidiaGpuOnWindows_RequestsTensorRtRtx()
    {
        var fingerprint = HardwareFingerprint.Create(
            "Windows",
            "x64",
            "NVIDIA GeForce RTX 4090",
            32L * 1024 * 1024 * 1024,
            24L * 1024 * 1024 * 1024);

        BenchmarkProviderPreference preference =
            HardwareProfilerService.ResolveProfilerBenchmarkProviderPreference(fingerprint, isWindows: true);

        Assert.Equal(BenchmarkProviderPreference.TensorRtRtx, preference);
    }

    [Fact]
    public void ResolveProfilerBenchmarkProviderPreference_NonNvidiaGpu_UsesAuto()
    {
        var fingerprint = HardwareFingerprint.Create(
            "Windows",
            "x64",
            "AMD Radeon RX 7900 XTX",
            32L * 1024 * 1024 * 1024,
            24L * 1024 * 1024 * 1024);

        BenchmarkProviderPreference preference =
            HardwareProfilerService.ResolveProfilerBenchmarkProviderPreference(fingerprint);

        Assert.Equal(BenchmarkProviderPreference.Auto, preference);
    }
}
