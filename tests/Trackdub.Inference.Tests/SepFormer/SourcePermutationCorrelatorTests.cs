using Trackdub.Inference.Onnx.SepFormer;

namespace Trackdub.Inference.Tests.SepFormer;

public sealed class SourcePermutationCorrelatorTests
{
    [Fact]
    public void AlignChunk_WithAmbiguousCorrelation_FlagsPermutationWarning()
    {
        float[] previous0 = [1f, 0f, 1f, 0f];
        float[] previous1 = [0f, 1f, 0f, 1f];
        float[] current0 = [0.5f, 0.5f, 0.5f, 0.5f];
        float[] current1 = [0.5f, 0.5f, 0.5f, 0.5f];

        (_, _, bool ambiguous) = SourcePermutationCorrelator.AlignChunk(
            current0,
            current1,
            previous0,
            previous1,
            priorAmbiguity: false);

        Assert.True(ambiguous);
    }

    [Fact]
    public void AlignChunk_WithClearSwap_SwapsSources()
    {
        float[] previous0 = [1f, 1f, 1f, 1f];
        float[] previous1 = [0f, 0f, 0f, 0f];
        float[] current0 = [0f, 0f, 0f, 0f];
        float[] current1 = [1f, 1f, 1f, 1f];

        (float[] aligned0, float[] aligned1, bool ambiguous) = SourcePermutationCorrelator.AlignChunk(
            current0,
            current1,
            previous0,
            previous1,
            priorAmbiguity: false);

        Assert.False(ambiguous);
        Assert.Equal(current1, aligned0);
        Assert.Equal(current0, aligned1);
    }
}
