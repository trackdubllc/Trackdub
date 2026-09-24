using BenchmarkDotNet.Attributes;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Benchmarks.Micro;

/// <summary>
/// Opt-in raw-graph benchmark for a single-input float ONNX model.
/// Set TRACKDUB_BDN_ONNX_MODEL, and optionally the input name and shape.
/// This is intentionally not part of the default local or pull-request filter.
/// </summary>
[BenchmarkCategory("Onnx")]
[MemoryDiagnoser]
public class OnnxModelBenchmarks
{
    private InferenceSession session = null!;
    private NamedOnnxValue[] inputs = null!;

    [GlobalSetup]
    public void Setup()
    {
        string modelPath = RequireEnvironmentVariable("TRACKDUB_BDN_ONNX_MODEL");
        if (!File.Exists(modelPath))
        {
            throw new FileNotFoundException("The opt-in ONNX benchmark model was not found.", modelPath);
        }

        session = new InferenceSession(modelPath);
        if (session.InputMetadata.Count != 1)
        {
            throw new InvalidOperationException(
                "The opt-in raw ONNX benchmark currently supports single-input graphs only.");
        }

        KeyValuePair<string, NodeMetadata> metadata = session.InputMetadata.Single();
        if (metadata.Value.ElementDataType != TensorElementType.Float)
        {
            throw new InvalidOperationException(
                $"The opt-in raw ONNX benchmark currently supports float inputs only; '{metadata.Key}' is {metadata.Value.ElementDataType}.");
        }

        string inputName = Environment.GetEnvironmentVariable("TRACKDUB_BDN_ONNX_INPUT_NAME") ?? metadata.Key;
        int[] dimensions = ParseShape(metadata.Value.Dimensions);
        int elementCount = dimensions.Aggregate(1, checked((left, right) => left * right));
        float[] values = new float[elementCount];
        inputs = [NamedOnnxValue.CreateFromTensor(inputName, new DenseTensor<float>(values, dimensions))];
    }

    [Benchmark]
    public int Run()
    {
        using IDisposableReadOnlyCollection<DisposableNamedOnnxValue> outputs = session.Run(inputs);
        return outputs.Count;
    }

    [GlobalCleanup]
    public void Cleanup() => session.Dispose();

    private static int[] ParseShape(int[] metadataDimensions)
    {
        string? configuredShape = Environment.GetEnvironmentVariable("TRACKDUB_BDN_ONNX_INPUT_SHAPE");
        if (string.IsNullOrWhiteSpace(configuredShape))
        {
            return metadataDimensions.Select(static dimension => dimension > 0 ? dimension : 1).ToArray();
        }

        int[] shape = configuredShape
            .Split('x', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(static value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
        if (shape.Length == 0 || shape.Any(static dimension => dimension <= 0))
        {
            throw new InvalidOperationException(
                $"Invalid TRACKDUB_BDN_ONNX_INPUT_SHAPE '{configuredShape}'; expected positive dimensions such as 1x128x500.");
        }

        return shape;
    }

    private static string RequireEnvironmentVariable(string variableName)
    {
        string? value = Environment.GetEnvironmentVariable(variableName);
        return string.IsNullOrWhiteSpace(value)
            ? throw new InvalidOperationException(
                $"Set {variableName} to run the opt-in real-model ONNX benchmark.")
            : value;
    }
}
