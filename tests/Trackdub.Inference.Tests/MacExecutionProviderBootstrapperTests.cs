using System.Runtime.Versioning;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.ExecutionProviders.Mac;

namespace Trackdub.Inference.Tests;

/// <summary>
/// Tests for MacExecutionProviderBootstrapper: CoreML always succeeds,
/// CPU always succeeds, all non-macOS EPs fall back to CPU.
/// </summary>
[SupportedOSPlatform("macos10.15")]
public sealed class MacExecutionProviderBootstrapperTests
{
    // ── CoreML always succeeds on macOS ──────────────────────────────────────

    [Fact]
    public async Task CoreMl_always_succeeds()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.CoreMl, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.CoreMl, result.SelectedProvider);
    }

    // ── CPU always succeeds ──────────────────────────────────────────────────

    [Fact]
    public async Task Cpu_always_succeeds()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.Cpu, allowDownloads: false, default);

        Assert.True(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    // ── Windows-only EPs rejected ────────────────────────────────────────────

    [Fact]
    public async Task DirectMl_falls_back_to_Cpu_on_macOS()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.DirectMl, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    [Fact]
    public async Task TensorRTRtx_falls_back_to_Cpu_on_macOS()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRTRtx, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    // ── Linux-only EPs rejected ──────────────────────────────────────────────

    [Fact]
    public async Task Cuda_falls_back_to_Cpu_on_macOS()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.Cuda, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    [Fact]
    public async Task TensorRt_falls_back_to_Cpu_on_macOS()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(ExecutionProviderKind.TensorRt, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.Equal(ExecutionProviderKind.Cpu, result.SelectedProvider);
    }

    // ── CheckReadiness mirrors Bootstrap ────────────────────────────────────

    [Fact]
    public async Task CheckReadiness_returns_same_result_as_Bootstrap_for_CoreMl()
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var bootstrap = await bootstrapper.BootstrapAsync(ExecutionProviderKind.CoreMl, allowDownloads: false, default);
        var readiness = await bootstrapper.CheckReadinessAsync(ExecutionProviderKind.CoreMl, default);

        Assert.Equal(bootstrap.Succeeded, readiness.Succeeded);
        Assert.Equal(bootstrap.SelectedProvider, readiness.SelectedProvider);
    }

    // ── Failure results carry a reason ──────────────────────────────────────

    [Theory]
    [InlineData(ExecutionProviderKind.DirectMl)]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    [InlineData(ExecutionProviderKind.OpenVino)]
    [InlineData(ExecutionProviderKind.Cuda)]
    [InlineData(ExecutionProviderKind.TensorRt)]
    [InlineData(ExecutionProviderKind.Migraphx)]
    [InlineData(ExecutionProviderKind.Qnn)]
    [InlineData(ExecutionProviderKind.OpenVinoCatalog)]
    [InlineData(ExecutionProviderKind.VitisAi)]
    public async Task Failed_result_carries_non_null_failure_reason(ExecutionProviderKind provider)
    {
        var bootstrapper = new MacExecutionProviderBootstrapper();

        var result = await bootstrapper.BootstrapAsync(provider, allowDownloads: false, default);

        Assert.False(result.Succeeded);
        Assert.NotNull(result.FailureReason);
    }
}
