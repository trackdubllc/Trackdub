using Trackdub.Contracts.Pipeline;
using Trackdub.Inference.Onnx.Qwen3Tts;
using Trackdub.Inference.Onnx.Translation;

namespace Trackdub.Inference.Onnx.Tests;

public sealed class Qwen3TtsVoiceCatalogTests
{
    [Fact]
    public void KnownAvailable_lists_all_nine_upstream_presets_with_gender()
    {
        Qwen3TtsVoiceCatalog catalog = Qwen3TtsVoiceCatalog.KnownAvailable();

        IReadOnlyList<VoiceCatalogEntry> voices = catalog.GetVoices();

        Assert.Equal(9, voices.Count);
        Assert.All(voices, voice => Assert.Equal("mul", voice.LanguageCode));
        Assert.True(catalog.TryGetVoice("qwen3:uncle_fu", out VoiceCatalogEntry? uncleFu));
        Assert.Equal("male", uncleFu.Gender);
        Assert.Equal("Uncle Fu (Chinese)", uncleFu.DisplayName);
        Assert.True(catalog.TryGetVoice("qwen3:ono_anna", out VoiceCatalogEntry? onoAnna));
        Assert.Equal("female", onoAnna.Gender);
    }

    [Fact]
    public void Load_enriches_known_speakers_and_keeps_unknown_ones()
    {
        string root = Path.Join(Path.GetTempPath(), $"qwen3-catalog-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Join(root, "embeddings"));
        File.WriteAllText(
            Path.Join(root, "embeddings", "speaker_ids.json"),
            """{ "vivian": 3065, "new_speaker": 4000 }""");
        try
        {
            Qwen3TtsVoiceCatalog catalog = Qwen3TtsVoiceCatalog.Load(root);

            Assert.True(catalog.TryGetVoice("qwen3:vivian", out VoiceCatalogEntry? vivian));
            Assert.Equal("female", vivian.Gender);
            Assert.Equal("Vivian (Chinese)", vivian.DisplayName);
            Assert.True(catalog.TryGetVoice("qwen3:new_speaker", out VoiceCatalogEntry? unknown));
            Assert.Equal("unknown", unknown.Gender);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public void Translation_coverage_includes_chinese_from_english()
    {
        Assert.True(TranslationLanguageCoverageMatrix.TryGetLanguage("zh", out TranslationLanguageDefinition? chinese));
        Assert.Equal("zh", chinese!.MadladTag);
        Assert.Contains(TranslationLanguageCoverageMatrix.GetTargets("en"), language => language.Code == "zh");
    }
}
