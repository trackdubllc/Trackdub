using Trackdub.Contracts;
using Trackdub.Contracts.StarterPacks;

namespace Trackdub.Infrastructure.StarterPacks;

public sealed class CloudCredentialReadinessService(ICloudApiKeyProvider apiKeyProvider) : ICloudCredentialReadiness
{
    private readonly ICloudApiKeyProvider apiKeyProvider =
        apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

    public async Task<CloudCredentialReadinessReport> EvaluateAsync(
        StarterPackCloudDefaults cloudDefaults,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(cloudDefaults);

        IReadOnlyList<string> requiredProviders = StarterPackCloudProviderRequirements.Resolve(cloudDefaults);
        var missing = new List<string>();
        foreach (string provider in requiredProviders)
        {
            string? apiKey = await apiKeyProvider
                .GetApiKeyAsync(provider, cancellationToken)
                .ConfigureAwait(false);
            if (string.IsNullOrWhiteSpace(apiKey))
            {
                missing.Add(provider);
            }
        }

        if (missing.Count == 0)
        {
            return new CloudCredentialReadinessReport(true, [], null);
        }

        string blockedReason = missing.Contains("openai", StringComparer.OrdinalIgnoreCase)
            ? "Configure OpenAI API keys in Cloud Models before applying this pack."
            : $"Configure API keys for {string.Join(", ", missing)} in Cloud Models before applying this pack.";

        return new CloudCredentialReadinessReport(false, missing, blockedReason);
    }
}

internal static class StarterPackCloudProviderRequirements
{
    public static IReadOnlyList<string> Resolve(StarterPackCloudDefaults defaults)
    {
        var providers = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (string provider in ResolveAsrProviders(defaults.Asr))
        {
            providers.Add(provider);
        }

        foreach (string provider in ResolveTranslationProviders(defaults.Translation))
        {
            providers.Add(provider);
        }

        foreach (string provider in ResolveTtsProviders(defaults.Tts))
        {
            providers.Add(provider);
        }

        return providers.ToList();
    }

    private static IEnumerable<string> ResolveAsrProviders(string key)
    {
        AsrModelOverride modelOverride = AsrModelOverrideSettings.FromKey(key);
        return modelOverride switch
        {
            AsrModelOverride.OpenAiWhisper => ["openai"],
            AsrModelOverride.GeminiAsr => ["gemini"],
            _ => []
        };
    }

    private static IEnumerable<string> ResolveTranslationProviders(string key)
    {
        TranslationModelOverride modelOverride = TranslationModelOverrideSettings.FromKey(key);
        return modelOverride switch
        {
            TranslationModelOverride.DeepL => ["deepl"],
            TranslationModelOverride.OpenAiGpt => ["openai"],
            TranslationModelOverride.GeminiTranslation => ["gemini"],
            _ => []
        };
    }

    private static IEnumerable<string> ResolveTtsProviders(string key)
    {
        TtsModelOverride modelOverride = NormalizeTtsKey(key);
        return modelOverride switch
        {
            TtsModelOverride.ElevenLabs => ["elevenlabs"],
            TtsModelOverride.OpenAiTts => ["openai"],
            TtsModelOverride.GoogleTts => ["google"],
            _ => []
        };
    }

    private static TtsModelOverride NormalizeTtsKey(string key) =>
        string.Equals(key.Trim(), "openai", StringComparison.OrdinalIgnoreCase)
            ? TtsModelOverride.OpenAiTts
            : TtsModelOverrideSettings.FromKey(key);
}
