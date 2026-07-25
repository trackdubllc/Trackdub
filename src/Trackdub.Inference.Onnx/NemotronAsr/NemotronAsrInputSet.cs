using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal sealed class NemotronAsrInputSet(IReadOnlyList<NamedOnnxValue> values) : IDisposable
{
    public IReadOnlyList<NamedOnnxValue> Values { get; } = values;

    public void Dispose()
    {
        foreach (IDisposable value in Values.OfType<IDisposable>())
        {
            value.Dispose();
        }
    }
}
