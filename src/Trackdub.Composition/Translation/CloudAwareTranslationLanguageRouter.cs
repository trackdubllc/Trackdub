using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Translation;

namespace Trackdub.Composition.Translation;

public sealed class CloudAwareTranslationLanguageRouter(
    ITranslationLanguageRouter localRouter,
    ICloudApiKeyProvider apiKeyProvider)
    : ITranslationLanguageRouter
{
    private readonly ITranslationLanguageRouter localRouter = localRouter ?? throw new ArgumentNullException(nameof(localRouter));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

    // Always delegates to the local router regardless of model alias.
    // The language picker is populated before route selection, so it reflects local model capabilities.
    // DeepL-specific language availability is enforced at route resolution time in ResolveRouteAsync.
    public Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
        string sourceLanguage,
        CancellationToken cancellationToken) =>
        localRouter.GetSupportedTargetLanguagesAsync(sourceLanguage, cancellationToken);

    public async Task<TranslationRouteSelection> ResolveRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        string? preferredModelAlias = null)
    {
        if (TranslationModelOverrideSettings.IsDeepLModelAlias(preferredModelAlias))
        {
            return await ResolveDeepLRouteAsync(sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        }

        if (TranslationModelOverrideSettings.IsGeminiTranslationAlias(preferredModelAlias))
        {
            return await ResolveGeminiRouteAsync(sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        }

        if (TranslationModelOverrideSettings.IsOpenAiGptAlias(preferredModelAlias))
        {
            return await ResolveOpenAiRouteAsync(sourceLanguage, targetLanguage, cancellationToken).ConfigureAwait(false);
        }

        return await localRouter.ResolveRouteAsync(
            sourceLanguage,
            targetLanguage,
            cancellationToken,
            preferredModelAlias).ConfigureAwait(false);
    }

    private async Task<TranslationRouteSelection> ResolveDeepLRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage) ?? "auto";
        string? normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage);
        if (normalizedTargetLanguage is null)
        {
            return Unavailable(
                normalizedSourceLanguage,
                targetLanguage,
                "DeepL target language is required.",
                DeepLCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.DeepLModelAlias,
                DeepLCloudTranslationEngine.EngineFamilyName);
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(DeepLCloudTranslationEngine.ProviderKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                "DeepL API key is not configured. Add a DeepL key in Cloud Models or set DEEPL_AUTH_KEY.",
                DeepLCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.DeepLModelAlias,
                DeepLCloudTranslationEngine.EngineFamilyName);
        }

        if (!DeepLCloudTranslationEngine.IsSupportedTargetLanguage(normalizedTargetLanguage))
        {
            return Unavailable(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                $"DeepL target language '{normalizedTargetLanguage}' is not in Trackdub's DeepL language catalog.",
                DeepLCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.DeepLModelAlias,
                DeepLCloudTranslationEngine.EngineFamilyName);
        }

        return new TranslationRouteSelection(
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            TranslationRoutingKind.Direct,
            IsAvailable: true,
            ProviderName: DeepLCloudTranslationEngine.ProviderName,
            RouteDetail: "DeepL Cloud API",
            PreferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
            EngineFamily: DeepLCloudTranslationEngine.EngineFamilyName);
    }

    private async Task<TranslationRouteSelection> ResolveGeminiRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage) ?? "auto";
        string? normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage);
        if (normalizedTargetLanguage is null)
        {
            return Unavailable(
                normalizedSourceLanguage,
                targetLanguage,
                "Gemini target language is required.",
                GeminiCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
                GeminiCloudTranslationEngine.EngineFamilyName);
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(GeminiCloudTranslationEngine.ProviderKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                "Gemini API key is not configured. Add a Gemini key in Cloud Models or set GEMINI_API_KEY.",
                GeminiCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
                GeminiCloudTranslationEngine.EngineFamilyName);
        }

        return new TranslationRouteSelection(
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            TranslationRoutingKind.Direct,
            IsAvailable: true,
            ProviderName: GeminiCloudTranslationEngine.ProviderName,
            RouteDetail: "Google Gemini Cloud API",
            PreferredModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
            EngineFamily: GeminiCloudTranslationEngine.EngineFamilyName);
    }

    private async Task<TranslationRouteSelection> ResolveOpenAiRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage) ?? "auto";
        string? normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage);
        if (normalizedTargetLanguage is null)
        {
            return Unavailable(
                normalizedSourceLanguage,
                targetLanguage,
                "OpenAI target language is required.",
                OpenAiCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.OpenAiGptCloudAlias,
                OpenAiCloudTranslationEngine.EngineFamilyName);
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(OpenAiCloudTranslationEngine.ProviderKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                "OpenAI API key is not configured. Add an OpenAI key in Cloud Models or set OPENAI_API_KEY.",
                OpenAiCloudTranslationEngine.ProviderName,
                TranslationModelOverrideSettings.OpenAiGptCloudAlias,
                OpenAiCloudTranslationEngine.EngineFamilyName);
        }

        return new TranslationRouteSelection(
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            TranslationRoutingKind.Direct,
            IsAvailable: true,
            ProviderName: OpenAiCloudTranslationEngine.ProviderName,
            RouteDetail: "OpenAI GPT Cloud API",
            PreferredModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias,
            EngineFamily: OpenAiCloudTranslationEngine.EngineFamilyName);
    }

    private static TranslationRouteSelection Unavailable(
        string sourceLanguage,
        string targetLanguage,
        string reason,
        string providerName,
        string preferredModelAlias,
        string engineFamily) =>
        new(
            sourceLanguage,
            targetLanguage,
            TranslationRoutingKind.Unavailable,
            IsAvailable: false,
            ProviderName: providerName,
            RouteDetail: $"{providerName} Cloud API unavailable",
            PreferredModelAlias: preferredModelAlias,
            UnavailableReason: reason,
            EngineFamily: engineFamily);

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        // Uppercase to match cloud convention; engine normalises again before sending.
        string normalized = languageCode.Trim().Replace('_', '-').ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}
