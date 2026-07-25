using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.StarterPacks;

public static class StarterPackDataDrivenApplyMapper
{
    public static StarterPackApplySettings ToApplySettings(StarterPackApplyBlock apply)
    {
        ArgumentNullException.ThrowIfNull(apply);

        IReadOnlyDictionary<string, string> overrides = apply.Overrides ?? new Dictionary<string, string>();
        IReadOnlyDictionary<string, string> cloudStages = apply.CloudStages ?? new Dictionary<string, string>();

        return new StarterPackApplySettings(
            ResolveAsr(overrides, cloudStages),
            ResolveTranslation(overrides, cloudStages),
            ResolveTts(overrides, cloudStages));
    }

    public static StarterPackCloudDefaults? ToCloudDefaults(StarterPackApplyBlock apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        if (apply.CloudStages is null || apply.CloudStages.Count == 0)
        {
            return null;
        }

        string asr = apply.CloudStages.TryGetValue("asr", out string? asrValue) ? asrValue : "auto";
        string translation = apply.CloudStages.TryGetValue("translation", out string? translationValue)
            ? translationValue
            : "auto";
        string tts = apply.CloudStages.TryGetValue("tts", out string? ttsValue) ? ttsValue : "auto";
        return new StarterPackCloudDefaults(asr, translation, tts);
    }

    public static void Validate(StarterPackApplyBlock apply)
    {
        ArgumentNullException.ThrowIfNull(apply);
        _ = ToApplySettings(apply);

        if (apply.CloudStages is null)
        {
            return;
        }

        foreach ((string stage, string value) in apply.CloudStages)
        {
            if (IsAutoToken(value))
            {
                continue;
            }

            switch (stage.ToLowerInvariant())
            {
                case "asr":
                    _ = MapCloudAsr(value);
                    break;
                case "translation":
                    _ = MapCloudTranslation(value);
                    break;
                case "tts":
                    _ = MapCloudTts(value);
                    break;
                default:
                    throw new InvalidOperationException($"Unknown cloud stage '{stage}'.");
            }
        }
    }

    private static AsrModelOverride ResolveAsr(
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> cloudStages)
    {
        if (TryOverrideKey(overrides, "asr", out string overrideKey))
        {
            return AsrModelOverrideSettings.FromKey(overrideKey);
        }

        if (cloudStages.TryGetValue("asr", out string? cloudKey) && !IsAutoToken(cloudKey))
        {
            return MapCloudAsr(cloudKey);
        }

        return AsrModelOverride.Auto;
    }

    private static TranslationModelOverride ResolveTranslation(
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> cloudStages)
    {
        if (TryOverrideKey(overrides, "translation", out string overrideKey))
        {
            return TranslationModelOverrideSettings.FromKey(overrideKey);
        }

        if (cloudStages.TryGetValue("translation", out string? cloudKey) && !IsAutoToken(cloudKey))
        {
            return MapCloudTranslation(cloudKey);
        }

        return TranslationModelOverride.Auto;
    }

    private static TtsModelOverride ResolveTts(
        IReadOnlyDictionary<string, string> overrides,
        IReadOnlyDictionary<string, string> cloudStages)
    {
        if (TryOverrideKey(overrides, "tts", out string overrideKey))
        {
            return string.Equals(overrideKey, "openai", StringComparison.OrdinalIgnoreCase)
                ? TtsModelOverride.OpenAiTts
                : TtsModelOverrideSettings.FromKey(overrideKey);
        }

        if (cloudStages.TryGetValue("tts", out string? cloudKey) && !IsAutoToken(cloudKey))
        {
            return MapCloudTts(cloudKey);
        }

        return TtsModelOverride.Auto;
    }

    private static bool TryOverrideKey(
        IReadOnlyDictionary<string, string> overrides,
        string stage,
        out string key)
    {
        if (overrides.TryGetValue(stage, out string? value) && !IsAutoToken(value))
        {
            key = value.Trim();
            return true;
        }

        key = string.Empty;
        return false;
    }

    private static bool IsAutoToken(string? value) =>
        string.IsNullOrWhiteSpace(value) || string.Equals(value.Trim(), "auto", StringComparison.OrdinalIgnoreCase);

    private static AsrModelOverride MapCloudAsr(string key)
    {
        AsrModelOverride modelOverride = AsrModelOverrideSettings.FromKey(key);
        if (modelOverride is not (AsrModelOverride.OpenAiWhisper or AsrModelOverride.GeminiAsr))
        {
            throw new InvalidOperationException($"Unsupported cloud ASR engine '{key}'.");
        }

        return modelOverride;
    }

    private static TranslationModelOverride MapCloudTranslation(string key)
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

    private static TtsModelOverride MapCloudTts(string key)
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
