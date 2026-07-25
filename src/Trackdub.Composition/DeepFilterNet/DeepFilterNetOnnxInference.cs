using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Trackdub.Inference.Onnx.DeepFilterNet;
using Trackdub.Inference.Onnx.Pool;

namespace Trackdub.Composition.DeepFilterNet;

/// <summary>
/// Runs the three-model DeepFilterNet3 ONNX export (Rikorose/DeepFilterNet3).
/// Verified model contract (input/output names and shapes read from the ONNX files):
///   enc:     feat_erb [1,1,T,32], feat_spec [1,2,T,96]
///            -> e0 [1,64,T,32], e1 [1,64,T,16], e2 [1,64,T,8], e3 [1,64,T,8],
///               emb [1,T,512], c0 [1,64,T,96], lsnr [1,T,1]
///   erb_dec: emb, e3, e2, e1, e0 -> m [1,1,T,32]
///   df_dec:  emb, c0 -> coefs [1,T,96,10] (order-major real/imag pairs per bin)
/// </summary>
internal static class DeepFilterNetOnnxInference
{
    public static (float[,,,] ErbGains, float[,,,,] DfCoefs) Run(
        DeepFilterNetModelSessions sessions,
        float[,,,] featErb,
        float[,,,] featSpec,
        int numFrames)
    {
        int erbElements = numFrames * DeepFilterNetSignalProcessor.ErbBands;
        int specElements = 2 * numFrames * DeepFilterNetSignalProcessor.NbDf;
        var flatErb = new float[erbElements];
        var flatSpec = new float[specElements];
        Buffer.BlockCopy(featErb, 0, flatErb, 0, erbElements * sizeof(float));
        Buffer.BlockCopy(featSpec, 0, flatSpec, 0, specElements * sizeof(float));

        var encInputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("feat_erb",
                new DenseTensor<float>(flatErb, [1, 1, numFrames, DeepFilterNetSignalProcessor.ErbBands])),
            NamedOnnxValue.CreateFromTensor("feat_spec",
                new DenseTensor<float>(flatSpec, [1, 2, numFrames, DeepFilterNetSignalProcessor.NbDf]))
        };

        // The encoder outputs feed both decoders, so they must stay alive (undisposed)
        // until both decoder runs complete.
        using var encOutputs = sessions.Enc.Session.RunWithRetry(encInputs);
        Tensor<float> emb = GetOutputTensor(encOutputs, "emb");
        Tensor<float> c0 = GetOutputTensor(encOutputs, "c0");

        float[,,,] erbGains;
        var erbDecInputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("emb", emb),
            NamedOnnxValue.CreateFromTensor("e3", GetOutputTensor(encOutputs, "e3")),
            NamedOnnxValue.CreateFromTensor("e2", GetOutputTensor(encOutputs, "e2")),
            NamedOnnxValue.CreateFromTensor("e1", GetOutputTensor(encOutputs, "e1")),
            NamedOnnxValue.CreateFromTensor("e0", GetOutputTensor(encOutputs, "e0"))
        };
        using (var erbDecOutputs = sessions.ErbDec.Session.RunWithRetry(erbDecInputs))
        {
            int maskElements = numFrames * DeepFilterNetSignalProcessor.ErbBands;
            float[] flatMask = ExtractFlat(GetOutputTensor(erbDecOutputs, "m"), maskElements, "erb_dec output 'm'");
            erbGains = new float[1, 1, numFrames, DeepFilterNetSignalProcessor.ErbBands];
            Buffer.BlockCopy(flatMask, 0, erbGains, 0, maskElements * sizeof(float));
        }

        float[,,,,] dfCoefs;
        var dfDecInputs = new[]
        {
            NamedOnnxValue.CreateFromTensor("emb", emb),
            NamedOnnxValue.CreateFromTensor("c0", c0)
        };
        using (var dfDecOutputs = sessions.DfDec.Session.RunWithRetry(dfDecInputs))
        {
            int coefElements = numFrames * DeepFilterNetSignalProcessor.NbDf * DeepFilterNetSignalProcessor.DfOrder * 2;
            float[] flatCoefs = ExtractFlat(GetOutputTensor(dfDecOutputs, "coefs"), coefElements, "df_dec output 'coefs'");
            dfCoefs = DeepFilterNetSignalProcessor.UnpackDfCoefs(flatCoefs, numFrames);
        }

        return (erbGains, dfCoefs);
    }

    private static Tensor<float> GetOutputTensor(
        IReadOnlyCollection<DisposableNamedOnnxValue> outputs,
        string name)
    {
        foreach (DisposableNamedOnnxValue output in outputs)
        {
            if (string.Equals(output.Name, name, StringComparison.Ordinal))
            {
                return output.AsTensor<float>();
            }
        }

        throw new InvalidOperationException(
            $"DeepFilterNet model did not produce expected output '{name}'. " +
            $"Available outputs: {string.Join(", ", outputs.Select(static o => o.Name))}.");
    }

    private static float[] ExtractFlat(Tensor<float> tensor, int expectedElements, string description)
    {
        if (tensor.Length != expectedElements)
        {
            throw new InvalidOperationException(
                $"DeepFilterNet {description} has {tensor.Length} elements; expected {expectedElements}.");
        }

        var flat = new float[expectedElements];
        if (tensor is DenseTensor<float> dense)
        {
            dense.Buffer.Span[..expectedElements].CopyTo(flat);
        }
        else
        {
            for (int i = 0; i < expectedElements; i++)
            {
                flat[i] = tensor.GetValue(i);
            }
        }

        return flat;
    }
}
