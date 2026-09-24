using BenchmarkDotNet.Attributes;
using Trackdub.Inference.Onnx.Audio;

namespace Trackdub.Benchmarks.Micro;

[BenchmarkCategory("Audio")]
[MemoryDiagnoser]
public class AudioResamplingBenchmarks
{
    private float[] source = null!;

    [Params(16_000, 160_000)]
    public int SampleCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        source = new float[SampleCount];
        for (int index = 0; index < source.Length; index++)
        {
            source[index] = MathF.Sin((index / 48_000f) * (2f * MathF.PI * 440f));
        }
    }

    [Benchmark]
    public int Resample48KhzTo16Khz() =>
        AudioResampler.Resample(source, 48_000, 16_000).Length;
}
