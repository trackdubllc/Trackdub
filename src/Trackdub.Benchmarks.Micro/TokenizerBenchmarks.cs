using BenchmarkDotNet.Attributes;
using Trackdub.Inference.Onnx.CosyVoice;
using Trackdub.Inference.Onnx.Kokoro;

namespace Trackdub.Benchmarks.Micro;

[BenchmarkCategory("Tokenizer")]
[MemoryDiagnoser]
public class KokoroTokenizerBenchmarks
{
    private KokoroTokenizer tokenizer = null!;
    private string phonemes = null!;

    [Params(128, 1_024)]
    public int TokenCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string modelRoot = RequireModelRoot("TRACKDUB_BDN_KOKORO_MODEL_ROOT");
        tokenizer = KokoroTokenizer.LoadAsync(modelRoot).GetAwaiter().GetResult();
        phonemes = string.Concat(Enumerable.Repeat("həlˈoʊ wɜːld ", TokenCount));
    }

    [Benchmark]
    public int Encode() => tokenizer.Encode(phonemes).Length;

    private static string RequireModelRoot(string variableName)
    {
        string? path = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"Set {variableName} to a cached Kokoro model root to run tokenizer benchmarks.");
        }

        return path;
    }
}

[BenchmarkCategory("Tokenizer")]
[MemoryDiagnoser]
public class CosyVoiceTokenizerBenchmarks
{
    private CosyVoiceWhisperTokenizer tokenizer = null!;
    private string text = null!;

    [Params(128, 1_024)]
    public int TokenCount { get; set; }

    [GlobalSetup]
    public void Setup()
    {
        string modelRoot = RequireModelRoot("TRACKDUB_BDN_COSYVOICE_MODEL_ROOT");
        tokenizer = CosyVoiceWhisperTokenizer.Load(modelRoot);
        text = string.Concat(Enumerable.Repeat("hello world ", TokenCount));
    }

    [Benchmark]
    public int Encode() => tokenizer.Encode(text).Length;

    private static string RequireModelRoot(string variableName)
    {
        string? path = Environment.GetEnvironmentVariable(variableName);
        if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
        {
            throw new InvalidOperationException(
                $"Set {variableName} to a cached CosyVoice model root to run tokenizer benchmarks.");
        }

        return path;
    }
}
