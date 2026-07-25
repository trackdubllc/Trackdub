namespace Trackdub.Inference.Onnx.SepFormer;

internal static class SourcePermutationCorrelator
{
    private const float AmbiguityThreshold = 0.05f;
    private const int TailSampleCount = 1600;

    public static (float[] Source0, float[] Source1, bool Ambiguous) AlignChunk(
        float[] source0,
        float[] source1,
        float[]? previousSource0Tail,
        float[]? previousSource1Tail,
        bool priorAmbiguity)
    {
        if (previousSource0Tail is null || previousSource1Tail is null || priorAmbiguity)
        {
            return (source0, source1, priorAmbiguity);
        }

        float directScore = Correlation(previousSource0Tail, source0) + Correlation(previousSource1Tail, source1);
        float swappedScore = Correlation(previousSource0Tail, source1) + Correlation(previousSource1Tail, source0);
        float delta = MathF.Abs(directScore - swappedScore);
        if (delta < AmbiguityThreshold)
        {
            return (source0, source1, true);
        }

        if (swappedScore > directScore)
        {
            return (source1, source0, false);
        }

        return (source0, source1, false);
    }

    public static float[] TakeTail(float[] samples) =>
        samples.Length <= TailSampleCount
            ? samples
            : samples[^TailSampleCount..];

    private static float Correlation(float[] left, float[] right)
    {
        int length = Math.Min(left.Length, right.Length);
        if (length == 0)
        {
            return 0f;
        }

        double sum = 0d;
        for (int i = 0; i < length; i++)
        {
            sum += left[i] * right[i];
        }

        return (float)sum / length;
    }
}
