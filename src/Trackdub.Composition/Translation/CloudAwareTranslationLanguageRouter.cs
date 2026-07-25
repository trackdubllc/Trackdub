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
        if (!TranslationModelOverrideSettings.IsDeepLModelAlias(preferredModelAlias))
        {
            return await localRouter.ResolveRouteAsync(
                sourceLanguage,
                targetLanguage,
                cancellationToken,
                preferredModelAlias).ConfigureAwait(false);
        }

        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage) ?? "auto";
        string? normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage);
        if (normalizedTargetLanguage is null)
        {
            return Unavailable(normalizedSourceLanguage, targetLanguage, "DeepL target language is required.");
        }

        string? apiKey = await apiKeyProvider.GetApiKeyAsync(DeepLCloudTranslationEngine.ProviderKey, cancellationToken)
            .ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            return Unavailable(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                "DeepL API key is not configured. Add a DeepL key in Cloud Models or set DEEPL_AUTH_KEY.");
        }

        if (!DeepLCloudTranslationEngine.IsSupportedTargetLanguage(normalizedTargetLanguage))
        {
            return Unavailable(
                normalizedSourceLanguage,
                normalizedTargetLanguage,
                $"DeepL target language '{normalizedTargetLanguage}' is not in Trackdub's DeepL language catalog.");
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

    private static TranslationRouteSelection Unavailable(
        string sourceLanguage,
        string targetLanguage,
        string reason) =>
        new(
            sourceLanguage,
            targetLanguage,
            TranslationRoutingKind.Unavailable,
            IsAvailable: false,
            ProviderName: DeepLCloudTranslationEngine.ProviderName,
            RouteDetail: "DeepL Cloud API unavailable",
            PreferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
            UnavailableReason: reason,
            EngineFamily: DeepLCloudTranslationEngine.EngineFamilyName);

    private static string? NormalizeLanguageCode(string? languageCode)
    {
        if (string.IsNullOrWhiteSpace(languageCode))
        {
            return null;
        }

        // Uppercase to match DeepLCloudTranslationEngine's convention; engine normalises again before sending.
        string normalized = languageCode.Trim().Replace('_', '-').ToUpperInvariant();
        return normalized.Length == 0 ? null : normalized;
    }
}
