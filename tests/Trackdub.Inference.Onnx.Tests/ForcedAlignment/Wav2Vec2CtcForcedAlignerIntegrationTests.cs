using System.Buffers.Binary;
using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.ForcedAlignment;

namespace Trackdub.Inference.Onnx.Tests.ForcedAlignment;

/// <summary>
/// Real-model smoke test for the wav2vec2 eSpeak CTC aligner. Skips (at discovery time)
/// when the model is not present in the user model cache — it never downloads anything.
/// </summary>
public sealed class Wav2Vec2CtcForcedAlignerIntegrationTests
{
    private const string ModelId = "wav2vec2-lv60-espeak-cv-ft-onnx";

    [Wav2Vec2ModelCacheFact]
    [Trait("Category", "Integration")]
    public async Task AlignAsync_RealModel_ShortWav_ReturnsNonEmptyPhonemes()
    {
        string modelRoot = RequiresModelCacheFactAttribute.ResolveModelRoot(ModelId);
        using var aligner = new Wav2Vec2CtcForcedAligner(modelRoot);

        Assert.True(
            aligner.IsAvailable,
            "Model files exist on disk but IsAvailable returned false. " +
            "Expected onnx/model_int8.onnx or onnx/model_fp16.onnx plus vocab.json.");

        string wavPath = Path.Combine(
            Path.GetTempPath(), $"trackdub-w2v2-int-{Guid.NewGuid():N}.wav");
        WriteSineWav(wavPath, durationSeconds: 1.0, sampleRate: 16_000, frequencyHz: 220.0);

        try
        {
            ForcedAlignmentResult result = await aligner.AlignAsync(
                new ForcedAlignmentRequest(
                    AudioPath: wavPath,
                    NormalizedTranscript: "cat",
                    LanguageCode: "en",
                    SegmentId: "integration-seg-1",
                    Options: new ForcedAlignmentOptions(
                        AllowPartial: true,
                        RequirePhonemeTimings: true)),
                CancellationToken.None);

            // A synthetic tone gives low confidence, so Partial is acceptable; what this
            // test proves is the mechanical chain: session load → vocab → CTC decode →
            // non-empty phoneme timings. It must never be Skipped or Failed.
            Assert.True(
                result.Status is ForcedAlignmentStatus.Success or ForcedAlignmentStatus.Partial,
                $"Unexpected status {result.Status}: {result.SkipReason}");
            Assert.NotEmpty(result.Phonemes);
            Assert.All(result.Phonemes, p => Assert.Equal("espeak-ipa", p.Inventory));
            Assert.All(result.Phonemes, p => Assert.True(p.End > p.Start));
            Assert.Equal("onnx-ctc-phoneme-aligner", result.ProviderId);
            Assert.Equal(ModelId, result.ModelId);
        }
        finally
        {
            try { File.Delete(wavPath); } catch { /* best-effort */ }
        }
    }

    private static void WriteSineWav(string path, double durationSeconds, int sampleRate, double frequencyHz)
    {
        int sampleCount = (int)(durationSeconds * sampleRate);
        int dataBytes = sampleCount * 2;

        using var stream = new FileStream(path, FileMode.Create, FileAccess.Write);
        using var writer = new BinaryWriter(stream);

        writer.Write("RIFF"u8);
        writer.Write(36 + dataBytes);
        writer.Write("WAVE"u8);
        writer.Write("fmt "u8);
        writer.Write(16);
        writer.Write((short)1);            // PCM
        writer.Write((short)1);            // mono
        writer.Write(sampleRate);
        writer.Write(sampleRate * 2);      // byte rate
        writer.Write((short)2);            // block align
        writer.Write((short)16);           // bits per sample
        writer.Write("data"u8);
        writer.Write(dataBytes);

        for (int i = 0; i < sampleCount; i++)
        {
            double t = (double)i / sampleRate;
            short sample = (short)(Math.Sin(2.0 * Math.PI * frequencyHz * t) * 12_000);
            writer.Write(sample);
        }
    }
}

/// <summary>
/// Marks a test that requires real model files in the user model cache
/// (TRACKDUB_MODEL_CACHE or %LOCALAPPDATA%/Trackdub/model-cache). Sets the xUnit Skip
/// reason at discovery time when any required file is missing. Never downloads.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class RequiresModelCacheFactAttribute : FactAttribute
{
    public RequiresModelCacheFactAttribute(string modelId, params string[] requiredRelativePaths)
    {
        string root = ResolveModelRoot(modelId);
        foreach (string relativePath in requiredRelativePaths)
        {
            string fullPath = Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(fullPath))
            {
                Skip = $"Model file '{relativePath}' for '{modelId}' not present in model cache ({root}). " +
                       "Download it via the Model Manager or `trackdub models download` to run this test.";
                return;
            }
        }
    }

    public static string ResolveModelRoot(string modelId)
    {
        string? configured = Environment.GetEnvironmentVariable("TRACKDUB_MODEL_CACHE");
        string cacheRoot = !string.IsNullOrWhiteSpace(configured)
            ? configured
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Trackdub", "model-cache");
        return Path.Combine(cacheRoot, modelId);
    }
}

/// <summary>
/// Discovery-time skip unless wav2vec2 vocab + (int8 OR fp16) ONNX are present.
/// </summary>
[AttributeUsage(AttributeTargets.Method)]
public sealed class Wav2Vec2ModelCacheFactAttribute : FactAttribute
{
    public Wav2Vec2ModelCacheFactAttribute()
    {
        const string modelId = "wav2vec2-lv60-espeak-cv-ft-onnx";
        string root = RequiresModelCacheFactAttribute.ResolveModelRoot(modelId);
        bool hasOnnx =
            File.Exists(Path.Combine(root, "onnx", "model_int8.onnx")) ||
            File.Exists(Path.Combine(root, "onnx", "model_fp16.onnx"));
        if (!hasOnnx || !File.Exists(Path.Combine(root, "vocab.json")))
        {
            Skip = $"Model files for '{modelId}' not present in model cache ({root}). " +
                   "Need vocab.json plus onnx/model_int8.onnx or onnx/model_fp16.onnx.";
        }
    }
}
