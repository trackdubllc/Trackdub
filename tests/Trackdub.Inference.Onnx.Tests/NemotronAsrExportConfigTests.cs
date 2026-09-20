using Trackdub.Inference.Onnx.NemotronAsr;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class NemotronAsrExportConfigTests
{
    [Fact]
    public void Load_reads_blank_id_vocab_size_and_normalize_from_bundled_shape()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"nemotron-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(
            configPath,
            """
            {
              "vocab_size": 13087,
              "blank_id": 13087,
              "preprocessor": { "normalize": "NA", "preemph": 0.97 }
            }
            """);
        try
        {
            NemotronAsrExportConfig config = NemotronAsrExportConfig.Load(configPath, hasPromptInput: true);

            Assert.Equal(13087, config.VocabSize);
            Assert.Equal(13087, config.BlankId);
            Assert.Equal(13087, config.DecoderStartTokenId);
            Assert.False(config.ApplyPerFeatureNormalization);
        }
        finally
        {
            File.Delete(configPath);
        }
    }

    [Fact]
    public void Load_missing_blank_id_falls_back_to_vocab_size_not_vocab_minus_one()
    {
        string configPath = Path.Combine(Path.GetTempPath(), $"nemotron-config-{Guid.NewGuid():N}.json");
        File.WriteAllText(configPath, """{ "vocab_size": 13087, "preprocessor": { "normalize": "per_feature" } }""");
        try
        {
            NemotronAsrExportConfig config = NemotronAsrExportConfig.Load(configPath, hasPromptInput: false);

            Assert.Equal(13087, config.BlankId);
            Assert.True(config.ApplyPerFeatureNormalization);
        }
        finally
        {
            File.Delete(configPath);
        }
    }
}
