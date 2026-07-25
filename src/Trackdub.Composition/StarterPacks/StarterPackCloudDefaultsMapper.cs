using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.StarterPacks;

public static class StarterPackCloudDefaultsMapper
{
    public static StarterPackApplySettings ToApplySettings(StarterPackCloudDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        return new StarterPackApplySettings(
            MapAsr(defaults.Asr),
            MapTranslation(defaults.Translation),
            MapTts(defaults.Tts));
    }

    public static void Validate(StarterPackCloudDefaults defaults)
    {
        ArgumentNullException.ThrowIfNull(defaults);

        _ = MapAsr(defaults.Asr);
        _ = MapTranslation(defaults.Translation);
        _ = MapTts(defaults.Tts);
    }

    private static AsrModelOverride MapAsr(string key)
    {
        AsrModelOverride modelOverride = AsrModelOverrideSettings.FromKey(key);
        if (modelOverride is not (AsrModelOverride.OpenAiWhisper or AsrModelOverride.GeminiAsr))
        {
            throw new InvalidOperationException($"Unsupported cloud ASR engine '{key}'.");
        }

        return modelOverride;
    }

    private static TranslationModelOverride MapTranslation(string key)
    {
        TranslationModelOverride modelOverride = TranslationModelOverrideSettings.FromKey(key);
        if (modelOverride is not (
            TranslationModelOverride.Auto
            or TranslationModelOverride.DeepL
            or TranslationModelOverride.OpenAiGpt
            or TranslationModelOverride.GeminiTranslation))
        {
            throw new InvalidOperationException($"Unsupported cloud translation engine '{key}'.");
        }

        return modelOverride;
    }

    private static TtsModelOverride MapTts(string key)
    {
        TtsModelOverride modelOverride = string.Equals(key.Trim(), "openai", StringComparison.OrdinalIgnoreCase)
            ? TtsModelOverride.OpenAiTts
            : TtsModelOverrideSettings.FromKey(key);

        if (modelOverride is not (
            TtsModelOverride.OpenAiTts
            or TtsModelOverride.ElevenLabs
            or TtsModelOverride.GoogleTts))
        {
            throw new InvalidOperationException($"Unsupported cloud TTS engine '{key}'.");
        }

        return modelOverride;
    }
}
