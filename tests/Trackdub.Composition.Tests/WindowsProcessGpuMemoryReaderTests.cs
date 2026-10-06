using System.Runtime.Versioning;
using Trackdub.Composition.Runtime;

namespace Trackdub.Composition.Tests;

/// <summary>
/// Adapter-LUID parsing for per-adapter GPU attribution: the counter set names each instance
/// <c>pid_&lt;pid&gt;_luid_&lt;high&gt;_&lt;low&gt;_phys_&lt;n&gt;</c>, and the pool maps those
/// LUIDs back to enumerated devices.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class WindowsProcessGpuMemoryReaderTests
{
    [Theory]
    // Hex components, the documented PDH form.
    [InlineData("pid_1234_luid_0x0_0x1a2b_phys_0", 0x1A2B)]
    [InlineData("pid_1234_luid_0x1_0x2_phys_3", 0x1_0000_0002)]
    // Decimal components are accepted too.
    [InlineData("pid_1234_luid_0_6699_phys_0", 6699)]
    [InlineData("pid_42_luid_1_2_phys_0", 0x1_0000_0002)]
    public void Parses_adapter_luids_from_process_gpu_instances(string instance, long expectedLuid)
    {
        Assert.True(WindowsProcessGpuMemoryReader.TryParseAdapterLuid(instance, out long luid));
        Assert.Equal(expectedLuid, luid);
    }

    [Theory]
    [InlineData("pid_1234_phys_0")]                        // missing LUID segments
    [InlineData("pid_1234_luid_0xZZ_0x1_phys_0")]           // unparsable component
    [InlineData("pid_1234_luid_0x1_phys_0")]                // missing low part
    [InlineData("not-an-instance")]                        // wrong shape entirely
    [InlineData("pid_1234_luid_0x1_0x2_phys_0_extra")]      // trailing segment
    public void Rejects_malformed_instance_names(string instance)
    {
        Assert.False(WindowsProcessGpuMemoryReader.TryParseAdapterLuid(instance, out _));
    }

    [Fact]
    public void Parsing_is_case_insensitive()
    {
        Assert.True(WindowsProcessGpuMemoryReader.TryParseAdapterLuid("PID_123_LUID_0x0_0x1_PHYS_0", out long luid));
        Assert.Equal(1, luid);
    }
}
