namespace Trackdub.Inference.Onnx.DeepFilterNet;

/// <summary>
/// Running feature-normalization state carried across chunked inference calls, mirroring
/// libDF's <c>band_mean_norm_erb</c> and <c>band_unit_norm</c> states: an exponential mean of
/// per-band ERB energies in dB, and an exponential mean of per-bin spectral magnitudes for the
/// deep-filter bins. Initial values replicate libDF's <c>MEAN_NORM_INIT</c> [-60, -90] and
/// <c>UNIT_NORM_INIT</c> [0.001, 0.0001] linear ramps.
/// </summary>
internal sealed class DeepFilterNetFeatureNormState
{
    private DeepFilterNetFeatureNormState(float[] erbMeanDb, float[] specUnitNorm)
    {
        ErbMeanDb = erbMeanDb;
        SpecUnitNorm = specUnitNorm;
    }

    public float[] ErbMeanDb { get; }

    public float[] SpecUnitNorm { get; }

    public static DeepFilterNetFeatureNormState CreateInitial() =>
        new(
            BuildLinearRamp(-60f, -90f, DeepFilterNetSignalProcessor.ErbBands),
            BuildLinearRamp(0.001f, 0.0001f, DeepFilterNetSignalProcessor.NbDf));

    private static float[] BuildLinearRamp(float start, float end, int count)
    {
        var values = new float[count];
        for (int i = 0; i < count; i++)
        {
            values[i] = start + (end - start) * i / (count - 1);
        }

        return values;
    }
}
