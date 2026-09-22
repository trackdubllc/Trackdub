using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.NemotronAsr;

/// <summary>
/// Double-buffered cache tensor for Nemotron's streaming encoder cache (cache_last_channel /
/// cache_last_time). Each encoder Run must read <see cref="CurrentInput"/> (this iteration's
/// cache) and write <see cref="CurrentOutput"/> (next iteration's cache) into a *different*
/// physical buffer than it reads from — ORT does not guarantee an op's kernel can alias its own
/// input and output safely. <see cref="Swap"/> then exposes the buffer just written as the input
/// for the next iteration, so the two ~5.25 MB / ~786 KB cache tensors are reused in place across
/// the whole decode loop instead of being copied element-by-element back to host every step.
/// </summary>
internal sealed class CachePingPongBuffer : IDisposable
{
    private readonly OrtValue boundA;
    private readonly OrtValue boundB;
    private bool inputIsA = true;

    public CachePingPongBuffer(int[] shape)
    {
        ArgumentNullException.ThrowIfNull(shape);
        int count = shape.Aggregate(1, static (prod, d) => checked(prod * d));
        long[] longShape = Array.ConvertAll(shape, static d => (long)d);
        boundA = OrtValue.CreateTensorValueFromMemory(new float[count], longShape);
        boundB = OrtValue.CreateTensorValueFromMemory(new float[count], longShape);
    }

    /// <summary>The buffer to bind as this iteration's cache input.</summary>
    public OrtValue CurrentInput => inputIsA ? boundA : boundB;

    /// <summary>
    /// The buffer to bind as this iteration's cache output. Always the buffer not currently
    /// serving as input, so a single Run never reads and writes the same memory.
    /// </summary>
    public OrtValue CurrentOutput => inputIsA ? boundB : boundA;

    /// <summary>Call after a successful Run: the buffer just written becomes the next input.</summary>
    public void Swap() => inputIsA = !inputIsA;

    public void Dispose()
    {
        boundA.Dispose();
        boundB.Dispose();
    }
}
