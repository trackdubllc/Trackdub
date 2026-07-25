using System.Linq;
using Microsoft.ML.OnnxRuntime;

namespace Trackdub.Inference.Onnx.Qwen3Asr;

internal sealed class Qwen3AsrInputSet(IReadOnlyList<NamedOnnxValue> values) : IDisposable
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
