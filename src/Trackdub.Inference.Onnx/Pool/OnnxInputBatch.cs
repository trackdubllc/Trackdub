using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Pool;

internal sealed class OnnxInputBatch : IDisposable
{
    private readonly List<NamedOnnxValue> values = [];

    public IReadOnlyList<NamedOnnxValue> Values => values;

    public void Add(NamedOnnxValue value) => values.Add(value);

    public void Dispose()
    {
        foreach (IDisposable value in values.OfType<IDisposable>())
        {
            value.Dispose();
        }

        values.Clear();
    }
}
