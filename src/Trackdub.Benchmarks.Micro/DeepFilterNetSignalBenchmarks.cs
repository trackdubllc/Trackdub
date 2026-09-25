using BenchmarkDotNet.Attributes;
using MathNet.Numerics;
using Trackdub.Inference.Onnx.DeepFilterNet;

namespace Trackdub.Benchmarks.Micro;

[BenchmarkCategory("SignalProcessing")]
[MemoryDiagnoser]
public class DeepFilterNetSignalBenchmarks
{
    private float[] samples = null!;
    private DeepFilterNetFeatureNormState normState = null!;
    private Complex32[,] stftFrames = null!;
    private float[,,,] erbGains = null!;
    private float[,,,,] dfCoefs = null!;

    [Params(480, 48_000)]
    public int SampleCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        samples = new float[SampleCount];
        for (int index = 0; index < samples.Length; index++)
        {
            samples[index] = MathF.Sin((index / 48_000f) * (2f * MathF.PI * 220f));
        }

        int frameCount = Math.Max(1, (SampleCount + DeepFilterNetSignalProcessor.HopSize - 1) /
            DeepFilterNetSignalProcessor.HopSize);
        stftFrames = new Complex32[frameCount, DeepFilterNetSignalProcessor.FreqBins];
        erbGains = new float[1, 1, frameCount, DeepFilterNetSignalProcessor.ErbBands];
        dfCoefs = new float[1, frameCount, DeepFilterNetSignalProcessor.DfOrder,
            DeepFilterNetSignalProcessor.NbDf, 2];

        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int bin = 0; bin < stftFrames.GetLength(1); bin++)
            {
                stftFrames[frame, bin] = new Complex32(0.01f * (frame + 1), 0.005f * (bin + 1));
            }
        }

        for (int frame = 0; frame < frameCount; frame++)
        {
            for (int band = 0; band < erbGains.GetLength(3); band++)
            {
                erbGains[0, 0, frame, band] = 0.9f;
            }

            for (int order = 0; order < dfCoefs.GetLength(2); order++)
            {
                for (int bin = 0; bin < dfCoefs.GetLength(3); bin++)
                {
                    dfCoefs[0, frame, order, bin, 0] = order == 0 ? 0.1f : 0f;
                    dfCoefs[0, frame, order, bin, 1] = 0f;
                }
            }
        }
    }

    [IterationSetup]
    public void ResetFeatureState()
    {
        normState = DeepFilterNetFeatureNormState.CreateInitial();
    }

    [Benchmark]
    public int ComputeFeatures()
    {
        DeepFilterNetSignalProcessor.ComputeFeatures(
            samples,
            normState,
            out float[,,,] featuresErb,
            out float[,,,] featuresSpec,
            out _);

        return featuresErb.Length + featuresSpec.Length;
    }

    [Benchmark]
    public int Synthesize()
    {
        float[] output = DeepFilterNetSignalProcessor.Synthesize(
            stftFrames, erbGains, dfCoefs, SampleCount);
        return output.Length;
    }
}
