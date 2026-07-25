using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Composition.StarterPacks;

public static class StarterPackApplyContract
{
    public static StarterPackApplySettings Resolve(string packId, string profileId)
    {
        if (string.Equals(packId, "basic", StringComparison.OrdinalIgnoreCase))
        {
            return new StarterPackApplySettings(
                AsrModelOverride.OnnxRuntime,
                TranslationModelOverride.Auto,
                TtsModelOverride.Kokoro);
        }

        if (string.Equals(packId, "balanced", StringComparison.OrdinalIgnoreCase))
        {
            return new StarterPackApplySettings(
                AsrModelOverride.GenAi,
                TranslationModelOverride.Auto,
                TtsModelOverride.Kokoro);
        }

        if (string.Equals(packId, "premium", StringComparison.OrdinalIgnoreCase))
        {
            return new StarterPackApplySettings(
                AsrModelOverride.GenAi,
                TranslationModelOverride.Madlad,
                TtsModelOverride.Chatterbox);
        }

        if (string.Equals(packId, "cloud", StringComparison.OrdinalIgnoreCase))
        {
            return StarterPackCloudDefaultsMapper.ToApplySettings(
                new StarterPackCloudDefaults("openai-whisper", "auto", "openai"));
        }

        throw new InvalidOperationException($"No apply contract for starter pack '{packId}'.");
    }
}

public sealed record StarterPackApplySettings(
    AsrModelOverride AsrModelOverride,
    TranslationModelOverride TranslationModelOverride,
    TtsModelOverride TtsModelOverride);
