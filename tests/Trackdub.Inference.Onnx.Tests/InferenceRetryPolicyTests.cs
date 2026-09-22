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
    [InlineData("[ErrorCode:RuntimeException] transient scheduling failure")]
    [InlineData("[ErrorCode:Fail] generic execution failure")]
    public void Non_device_runtime_failures_are_retried(string message)
    {
        Assert.True(InferenceRetryPolicy.IsTransientMessage(message));
    }

    [Theory]
    [InlineData("[ErrorCode:InvalidArgument] input shape mismatch")]
    [InlineData("[ErrorCode:InvalidGraph] node has no implementation")]
    [InlineData("[ErrorCode:NotImplemented] op is unsupported")]
    public void Permanent_failures_are_not_retried(string message)
    {
        Assert.False(InferenceRetryPolicy.IsTransientMessage(message));
    }
}
