using Microsoft.ML.OnnxRuntime.Tensors;

namespace Trackdub.Inference.Onnx.NemotronAsr;

internal static class NemotronAsrEncodedTensorLayout
{
    internal readonly record struct EncodedLayout(bool TimeMajor, int AvailableFrames, int HiddenSize);

    internal static EncodedLayout Resolve(ReadOnlySpan<int> dimensions, int encodedLength, int hiddenDim)
    {
        bool timeMajor = ResolveTimeMajor(dimensions, hiddenDim);
        int timeAxis = timeMajor ? 1 : 2;
        int availableFrames = Math.Min(encodedLength, dimensions[timeAxis]);
        return new EncodedLayout(timeMajor, availableFrames, hiddenDim);
    }

    internal static bool ResolveTimeMajor(ReadOnlySpan<int> dimensions, int hiddenDim)
    {
        if (dimensions.Length != 3)
        {
            throw new InvalidOperationException($"Unexpected Nemotron encoded rank {dimensions.Length}.");
        }

        if (dimensions[2] == hiddenDim)
        {
            return true;
        }

        if (dimensions[1] == hiddenDim)
        {
            return false;
        }

        throw new InvalidOperationException(
            $"Nemotron encoder output dimensions [{dimensions[0]}, {dimensions[1]}, {dimensions[2]}] do not match hidden dim {hiddenDim}.");
    }

    internal static DenseTensor<float> SliceFrame(Tensor<float> encoded, EncodedLayout layout, int frameIndex)
    {
        var data = new float[layout.HiddenSize];
        for (int hiddenIndex = 0; hiddenIndex < layout.HiddenSize; hiddenIndex++)
        {
            data[hiddenIndex] = layout.TimeMajor
                ? encoded[0, frameIndex, hiddenIndex]
                : encoded[0, hiddenIndex, frameIndex];
        }

        return new DenseTensor<float>(data, [1, layout.HiddenSize, 1]);
    }
}
