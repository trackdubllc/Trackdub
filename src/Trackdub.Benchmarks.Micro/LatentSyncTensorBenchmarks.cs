using BenchmarkDotNet.Attributes;
using Trackdub.Inference.Onnx.LipSynthesis;

namespace Trackdub.Benchmarks.Micro;

[BenchmarkCategory("Tensor")]
[MemoryDiagnoser]
public class LatentSyncTensorBenchmarks
{
    private byte[] rgba = null!;
    private float[] pcm = null!;

    [Params(64, 256)]
    public int SourceWidth { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        int height = SourceWidth;
        rgba = new byte[checked(SourceWidth * height * 4)];
        for (int index = 0; index < rgba.Length; index += 4)
        {
            rgba[index] = (byte)(index % 256);
            rgba[index + 1] = (byte)(255 - (index % 256));
            rgba[index + 2] = (byte)(index % 128);
            rgba[index + 3] = 255;
        }

        pcm = new float[16_000];
        for (int index = 0; index < pcm.Length; index++)
        {
            pcm[index] = MathF.Sin((index / 16_000f) * (2f * MathF.PI * 180f));
        }
    }

    [Benchmark]
    public int NormalizeRgba() =>
        LatentSyncTensorPreprocessor.RgbaToNormalizedTensor(rgba, SourceWidth, SourceWidth).Length;

    [Benchmark]
    public int ComputeWhisperMel() =>
        LatentSyncTensorPreprocessor.ComputeWhisperMelSpectrogram(pcm).Length;
}
