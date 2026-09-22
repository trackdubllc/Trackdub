using Trackdub.Domain;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class InferenceRetryPolicyTests
{
    [Theory]
    [InlineData("[ErrorCode:RuntimeException] CUDA error: out of memory")]
    [InlineData("[ErrorCode:RuntimeException] D3D12 allocation failed")]
    [InlineData("[ErrorCode:RuntimeException] insufficient memory for allocation")]
    [InlineData("[ErrorCode:RuntimeException] DXGI_ERROR_DEVICE_REMOVED")]
    [InlineData("[ErrorCode:RuntimeException] CUDA driver reported device lost")]
    [InlineData("[ErrorCode:RuntimeException] GPU device hung")]
    public void Device_level_failures_are_not_retried(string message)
    {
        // These survive a re-run against the same session, so they must reach the
        // device-fallback path immediately rather than consume the backoff budget.
        Assert.False(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Theory]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    [InlineData(null)]
    public void Device_level_failures_are_not_retried_regardless_of_provider(ExecutionProviderKind? provider)
    {
        Assert.False(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:RuntimeException] CUDA error: out of memory", provider));
    }

    [Theory]
    [InlineData("[ErrorCode:RuntimeException] transient scheduling failure")]
    [InlineData("[ErrorCode:Fail] generic execution failure")]
    public void Non_device_runtime_failures_are_retried_when_provider_is_unknown(string message)
    {
        // No provider hint preserves the pre-existing permissive behavior for callers that
        // haven't been updated to pass one.
        Assert.True(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Fact]
    public void Unclassified_runtime_exception_is_retried_on_dml()
    {
        // A DML RuntimeException with no OOM/device-removed markers is typically a momentary
        // scheduling hiccup that a bare re-run against the same session can clear.
        Assert.True(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:RuntimeException] transient scheduling failure",
            ExecutionProviderKind.DirectMl));
    }

    [Fact]
    public void Unclassified_runtime_exception_propagates_immediately_on_tensorrt_rtx()
    {
        // TensorRT-RTX (CUDA-backed) can leave a sticky execution-context error that survives a
        // bare re-run on the same session, so this must NOT be retried: it should propagate on
        // the first throw so the device-fallback path can rebuild the session elsewhere.
        Assert.False(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:RuntimeException] transient scheduling failure",
            ExecutionProviderKind.TensorRTRtx));
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ExecutionProviderKind.Cpu)]
    [InlineData(ExecutionProviderKind.DirectMl)]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    public void Fail_error_code_is_retried_for_every_provider(ExecutionProviderKind? provider)
    {
        Assert.True(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:Fail] generic execution failure", provider));
    }

    [Theory]
    [InlineData("[ErrorCode:InvalidArgument] input shape mismatch")]
    [InlineData("[ErrorCode:InvalidGraph] node has no implementation")]
    [InlineData("[ErrorCode:NotImplemented] op is unsupported")]
    public void Permanent_failures_are_not_retried(string message)
    {
        Assert.False(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Theory]
    [InlineData(ExecutionProviderKind.TensorRTRtx)]
    [InlineData(ExecutionProviderKind.DirectMl)]
    [InlineData(ExecutionProviderKind.Cpu)]
    public void Permanent_failures_are_not_retried_regardless_of_provider(ExecutionProviderKind provider)
    {
        Assert.False(InferenceRetryPolicy.IsTransientMessage(
            "[ErrorCode:InvalidArgument] input shape mismatch", provider));
    }
}
