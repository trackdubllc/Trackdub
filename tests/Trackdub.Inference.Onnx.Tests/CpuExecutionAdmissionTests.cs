using Microsoft.ML.OnnxRuntime;
using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class CpuExecutionAdmissionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Constructor_nonpositive_limit_throws(int limit) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new CpuExecutionAdmission(limit));

    [Fact]
    public async Task Cpu_limit1_second_waiter_blocks_until_first_releases()
    {
        var admission = new CpuExecutionAdmission(1);
        using IDisposable? first = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        Task<IDisposable?> waiter = Task.Run(
            () => admission.AcquireAsync(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken).AsTask());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        first!.Dispose();
        using IDisposable? second = await waiter;
        Assert.NotNull(second);

        second!.Dispose();
        second.Dispose();

        using IDisposable? third = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);
        Assert.NotNull(third);
    }

    [Fact]
    public async Task Dnnl_shares_the_cpu_gate()
    {
        var admission = new CpuExecutionAdmission(1);
        using IDisposable? first = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);

        Task<IDisposable?> waiter = Task.Run(
            () => admission.AcquireAsync(ExecutionProviderKind.Dnnl, TestContext.Current.CancellationToken).AsTask());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        first!.Dispose();
        using IDisposable? second = await waiter;
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Gpu_providers_bypass_while_cpu_occupied()
    {
        var admission = new CpuExecutionAdmission(1);
        using IDisposable? held = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);

        Assert.Null(admission.Acquire(ExecutionProviderKind.Cuda, TestContext.Current.CancellationToken));
        Assert.Null(admission.Acquire(ExecutionProviderKind.DirectMl, TestContext.Current.CancellationToken));
        Assert.Null(await admission.AcquireAsync(ExecutionProviderKind.TensorRTRtx, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Unknown_provider_throttles()
    {
        var admission = new CpuExecutionAdmission(1);
        using IDisposable? first = admission.Acquire(provider: null, TestContext.Current.CancellationToken);
        Assert.NotNull(first);

        Task<IDisposable?> waiter = Task.Run(
            () => admission.AcquireAsync(provider: null, TestContext.Current.CancellationToken).AsTask());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        first!.Dispose();
        using IDisposable? second = await waiter;
        Assert.NotNull(second);
    }

    [Fact]
    public async Task Canceled_waiter_consumes_no_permit()
    {
        var admission = new CpuExecutionAdmission(1);
        using IDisposable? first = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);

        using var canceled = new CancellationTokenSource();
        Task<IDisposable?> waiter = admission
            .AcquireAsync(ExecutionProviderKind.Cpu, canceled.Token)
            .AsTask();
        await canceled.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiter);

        first!.Dispose();
        using IDisposable? after = await admission.AcquireAsync(
            ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);
        Assert.NotNull(after);
    }

    [Fact]
    public void PreCanceled_token_throws_for_cpu_and_gpu()
    {
        var admission = new CpuExecutionAdmission(1);
        using var canceled = new CancellationTokenSource();
        canceled.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => admission.Acquire(ExecutionProviderKind.Cpu, canceled.Token));
        Assert.Throws<OperationCanceledException>(
            () => admission.Acquire(ExecutionProviderKind.Cuda, canceled.Token));
    }

    [Fact]
    public async Task Registered_effective_provider_wins_over_explicit_hint()
    {
        var admission = new CpuExecutionAdmission(1);
        using var session = new InferenceSession(IdentityOnnxModel);

        admission.RegisterSession(session, ExecutionProviderKind.Cuda);
        Assert.Null(admission.Acquire(session, null, TestContext.Current.CancellationToken));

        admission.RegisterSession(session, ExecutionProviderKind.Cpu);
        using IDisposable? held = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);

        Task<IDisposable?> hinted = Task.Run(
            () => admission.AcquireAsync(session, ExecutionProviderKind.Cuda, TestContext.Current.CancellationToken).AsTask());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(hinted.IsCompleted);

        held!.Dispose();
        using IDisposable? released = await hinted;
        Assert.NotNull(released);
    }

    [Fact]
    public async Task Unregistered_session_uses_explicit_hint()
    {
        var admission = new CpuExecutionAdmission(1);
        using var session = new InferenceSession(IdentityOnnxModel);

        Assert.Null(admission.Acquire(session, ExecutionProviderKind.Cuda, TestContext.Current.CancellationToken));

        using IDisposable? held = admission.Acquire(ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);
        Task<IDisposable?> waiter = Task.Run(
            () => admission.AcquireAsync(session, ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken).AsTask());
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.False(waiter.IsCompleted);

        held!.Dispose();
        using IDisposable? released = await waiter;
        Assert.NotNull(released);
    }

    [Fact]
    public void RegisterSession_replaces_existing_registration()
    {
        var admission = new CpuExecutionAdmission(1);
        using var session = new InferenceSession(IdentityOnnxModel);
        admission.RegisterSession(session, ExecutionProviderKind.Cpu);
        admission.RegisterSession(session, ExecutionProviderKind.Cuda);

        Assert.Null(admission.Acquire(session, null, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Twenty_cycles_acquire_release_leave_gate_open()
    {
        var admission = new CpuExecutionAdmission(1);
        for (int i = 0; i < 20; i++)
        {
            using IDisposable? permit = await admission.AcquireAsync(
                ExecutionProviderKind.Cpu, TestContext.Current.CancellationToken);
            Assert.NotNull(permit);
        }
    }

    private static readonly byte[] IdentityOnnxModel =
    [
        0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
        0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
        0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ];
}
