using Trackdub.Domain;
using Trackdub.Inference.Runtime.Migraphx;
using Trackdub.Inference.Runtime.Planning;

namespace Trackdub.Inference.Tests;

public sealed class MigraphxProviderOrderingTests
{
    [Fact]
    public void ApplyAmdMigraphxFirst_MovesMigraphxBeforeDirectMl_OnAmdGpu()
    {
        ExecutionProviderKind[] ordered =
        [
            ExecutionProviderKind.TensorRTRtx,
            ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.Migraphx,
            ExecutionProviderKind.Cpu
        ];

        IReadOnlyList<ExecutionProviderKind> result =
            MigraphxProviderOrdering.ApplyAmdMigraphxFirst(ordered, preferMigraphxOnAmdGpu: true);

        Assert.Equal(
            [
                ExecutionProviderKind.TensorRTRtx,
                ExecutionProviderKind.Migraphx,
                ExecutionProviderKind.DirectMl,
                ExecutionProviderKind.Cpu
            ],
            result);
    }

    [Fact]
    public void ApplyAmdMigraphxFirst_PreservesOrder_WhenMigraphxAlreadyBeforeDirectMl()
    {
        ExecutionProviderKind[] ordered =
        [
            ExecutionProviderKind.Migraphx,
            ExecutionProviderKind.DirectMl,
            ExecutionProviderKind.Cpu
        ];

        IReadOnlyList<ExecutionProviderKind> result =
            MigraphxProviderOrdering.ApplyAmdMigraphxFirst(ordered, preferMigraphxOnAmdGpu: true);

        Assert.Equal(ordered, result);
    }

    [Fact]
    public void ApplyAmdMigraphxFirst_LeavesOrder_WhenNotAmdGpu()
    {
        ExecutionProviderKind[] ordered =
        [
            ExecutionProviderKind.Migraphx,
            ExecutionProviderKind.DirectMl
        ];

        IReadOnlyList<ExecutionProviderKind> result =
            MigraphxProviderOrdering.ApplyAmdMigraphxFirst(ordered, preferMigraphxOnAmdGpu: false);

        Assert.Equal(ordered, result);
    }

    [Fact]
    public void ShouldPreferMigraphxOnAmdGpu_DetectsAmdFromGpuDescription()
    {
        var profile = new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "AMD Radeon RX 7900 XTX");

        Assert.True(MigraphxProviderOrdering.ShouldPreferMigraphxOnAmdGpu(profile));
    }

    [Fact]
    public void ShouldPreferMigraphxOnAmdGpu_DetectsRadeonDriverDescWithoutAmdPrefix()
    {
        var profile = new HardwareProfile("windows", "x64", HasGpu: true, GpuDescription: "Radeon RX 7900 XTX");

        Assert.True(MigraphxProviderOrdering.ShouldPreferMigraphxOnAmdGpu(profile));
    }
}
