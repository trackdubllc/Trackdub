using Microsoft.ML.OnnxRuntime;
using Trackdub.Inference.Onnx.NemotronAsr;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class CachePingPongBufferTests
{
    [Fact]
    public void CurrentInput_and_CurrentOutput_are_never_the_same_buffer()
    {
        using var buffer = new CachePingPongBuffer([2, 3]);

        Assert.NotSame(buffer.CurrentInput, buffer.CurrentOutput);

        buffer.Swap();
        Assert.NotSame(buffer.CurrentInput, buffer.CurrentOutput);

        buffer.Swap();
        Assert.NotSame(buffer.CurrentInput, buffer.CurrentOutput);
    }

    [Fact]
    public void Swap_exposes_the_buffer_just_written_as_the_next_input()
    {
        using var buffer = new CachePingPongBuffer([4]);

        // Simulate ORT writing an encoder output into CurrentOutput.
        Span<float> written = buffer.CurrentOutput.GetTensorMutableDataAsSpan<float>();
        written[0] = 1f;
        written[1] = 2f;
        written[2] = 3f;
        written[3] = 4f;

        buffer.Swap();

        Span<float> nowInput = buffer.CurrentInput.GetTensorMutableDataAsSpan<float>();
        Assert.Equal([1f, 2f, 3f, 4f], nowInput.ToArray());
    }

    [Fact]
    public void Repeated_swaps_ping_pong_between_exactly_two_physical_buffers()
    {
        using var buffer = new CachePingPongBuffer([1]);

        OrtValue first = buffer.CurrentInput;
        OrtValue second = buffer.CurrentOutput;

        buffer.Swap();
        Assert.Same(second, buffer.CurrentInput);
        Assert.Same(first, buffer.CurrentOutput);

        buffer.Swap();
        Assert.Same(first, buffer.CurrentInput);
        Assert.Same(second, buffer.CurrentOutput);

        buffer.Swap();
        Assert.Same(second, buffer.CurrentInput);
        Assert.Same(first, buffer.CurrentOutput);
    }

    [Fact]
    public void Initial_buffers_are_zero_filled()
    {
        using var buffer = new CachePingPongBuffer([3, 2]);

        Assert.All(buffer.CurrentInput.GetTensorMutableDataAsSpan<float>().ToArray(), static v => Assert.Equal(0f, v));
        Assert.All(buffer.CurrentOutput.GetTensorMutableDataAsSpan<float>().ToArray(), static v => Assert.Equal(0f, v));
    }

    [Fact]
    public void Dispose_does_not_throw()
    {
        var buffer = new CachePingPongBuffer([2, 2]);
        buffer.Dispose();
    }
}
