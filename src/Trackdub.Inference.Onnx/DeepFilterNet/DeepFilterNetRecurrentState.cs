namespace Trackdub.Inference.Onnx.DeepFilterNet;

/// <summary>
/// Carried recurrent hidden states for the three DeepFilterNet3 models. Each GRU in the export
/// has hidden size <see cref="DeepFilterNetOnnxGraphTransform.GruHiddenSize"/> and is driven
/// through explicit graph inputs/outputs (see <see cref="DeepFilterNetOnnxGraphTransform"/>);
/// this holder is what makes streaming inference state-preserving across bounded windows.
/// </summary>
internal sealed class DeepFilterNetRecurrentState
{
    public DeepFilterNetRecurrentState()
    {
        Enc = new float[DeepFilterNetOnnxGraphTransform.GruHiddenSize];
        Erb0 = new float[DeepFilterNetOnnxGraphTransform.GruHiddenSize];
        Erb1 = new float[DeepFilterNetOnnxGraphTransform.GruHiddenSize];
        Df0 = new float[DeepFilterNetOnnxGraphTransform.GruHiddenSize];
        Df1 = new float[DeepFilterNetOnnxGraphTransform.GruHiddenSize];
    }

    public float[] Enc { get; }

    public float[] Erb0 { get; }

    public float[] Erb1 { get; }

    public float[] Df0 { get; }

    public float[] Df1 { get; }
}