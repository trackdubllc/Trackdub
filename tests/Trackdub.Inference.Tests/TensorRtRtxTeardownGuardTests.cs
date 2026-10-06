using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.TensorRtRtx;

namespace Trackdub.Inference.Tests;

[Collection(nameof(TensorRtRtxTeardownGuardCollection))]
public sealed class TensorRtRtxTeardownGuardTests : IDisposable
{
    public TensorRtRtxTeardownGuardTests() => TensorRtRtxTeardownGuard.ResetForTests();

    public void Dispose() => TensorRtRtxTeardownGuard.ResetForTests();

    [Theory]
    [InlineData("CUDA failure 2: out of memory ; GPU=0 ; hostname=x", true)]
    [InlineData("cudaErrorMemoryAllocation while compiling engine", true)]
    [InlineData("CUDA_ERROR_OUT_OF_MEMORY", true)]
    [InlineData("Kernel not found for op: Squeeze(13) under NvTensorRTRTXExecutionProvider", false)]
    [InlineData("unrelated IO error", false)]
    public void MarkPoisonedIfCudaOutOfMemory_classifies_by_message(string message, bool expected)
    {
        Assert.Equal(expected, TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(new InvalidOperationException(message)));
        Assert.Equal(expected, TensorRtRtxTeardownGuard.IsPoisoned);
    }

    [Fact]
    public void MarkPoisonedIfCudaOutOfMemory_inspects_inner_exceptions()
    {
        var wrapped = new InvalidOperationException("session init failed", new InvalidOperationException("CUDA failure: out of memory"));

        Assert.True(TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(wrapped));
    }

    [Fact]
    public void ShouldSkipNativeTeardown_only_for_tracked_sessions_in_a_poisoned_runtime()
    {
        using InferenceSession tracked = CreateSession();
        using InferenceSession untracked = CreateSession();
        TensorRtRtxTeardownGuard.Track(tracked);

        Assert.False(TensorRtRtxTeardownGuard.ShouldSkipNativeTeardown(tracked));

        TensorRtRtxTeardownGuard.MarkPoisonedIfCudaOutOfMemory(new InvalidOperationException("out of memory"));

        Assert.True(TensorRtRtxTeardownGuard.ShouldSkipNativeTeardown(tracked));
        // DirectML/CPU sessions (including fallbacks) are never abandoned.
        Assert.False(TensorRtRtxTeardownGuard.ShouldSkipNativeTeardown(untracked));
    }

    private static InferenceSession CreateSession() => new(
    [
        0x08, 0x07, 0x3A, 0x3A, 0x0A, 0x10, 0x0A, 0x01, 0x78, 0x12, 0x01, 0x79, 0x22, 0x08,
        0x49, 0x64, 0x65, 0x6E, 0x74, 0x69, 0x74, 0x79, 0x12, 0x04, 0x74, 0x65, 0x73, 0x74,
        0x5A, 0x0F, 0x0A, 0x01, 0x78, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01, 0x12, 0x04, 0x0A,
        0x02, 0x08, 0x01, 0x62, 0x0F, 0x0A, 0x01, 0x79, 0x12, 0x0A, 0x0A, 0x08, 0x08, 0x01,
        0x12, 0x04, 0x0A, 0x02, 0x08, 0x01, 0x42, 0x04, 0x0A, 0x00, 0x10, 0x09,
    ]);
}

/// <summary>The poisoned flag is process-global, so tests that touch it must not run in parallel.</summary>
[CollectionDefinition(nameof(TensorRtRtxTeardownGuardCollection), DisableParallelization = true)]
public sealed class TensorRtRtxTeardownGuardCollection;
