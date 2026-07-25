using Trackdub.Contracts.Pipeline;

namespace Trackdub.TestDoubles;

public sealed class FakeTranslationLanguageRouter : ITranslationLanguageRouter
{
    private readonly Dictionary<string, List<TranslationTargetLanguageOption>> supportedTargetsBySourceLanguage =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<(string SourceLanguage, string TargetLanguage), TranslationRouteSelection> routes =
        new();

    public FakeTranslationLanguageRouter()
    {
        SetSupportedTargetLanguages(
            "en",
            new TranslationTargetLanguageOption("es", "Spanish", TranslationRoutingKind.Direct, IsAvailable: true, "Direct Opus-MT"),
            new TranslationTargetLanguageOption("fr", "French", TranslationRoutingKind.Pivot, IsAvailable: true, "MADLAD-400 pivot"),
            new TranslationTargetLanguageOption("ja", "Japanese", TranslationRoutingKind.Unavailable, IsAvailable: false, "MADLAD-400 pivot is unavailable for Japanese."));
        SetSupportedTargetLanguages(
            "es",
            new TranslationTargetLanguageOption("en", "English", TranslationRoutingKind.Direct, IsAvailable: true, "Direct Opus-MT"),
            new TranslationTargetLanguageOption("fr", "French", TranslationRoutingKind.Pivot, IsAvailable: true, "MADLAD-400 pivot"));

        SetRoute(new TranslationRouteSelection("en", "es", TranslationRoutingKind.Direct, IsAvailable: true, "opus-mt", "Direct Opus-MT", "fake-opus-en-es", "opus-en-es", EngineFamily: "opus-mt"));
        SetRoute(new TranslationRouteSelection("en", "fr", TranslationRoutingKind.Pivot, IsAvailable: true, "madlad400", "MADLAD-400 pivot", "fake-madlad400", "madlad400-mt", EngineFamily: "madlad"));
        SetRoute(new TranslationRouteSelection("en", "ja", TranslationRoutingKind.Unavailable, IsAvailable: false, "none", "Unavailable", UnavailableReason: "MADLAD-400 pivot is unavailable for Japanese."));
        SetRoute(new TranslationRouteSelection("es", "en", TranslationRoutingKind.Direct, IsAvailable: true, "opus-mt", "Direct Opus-MT", "fake-opus-es-en", "opus-es-en", EngineFamily: "opus-mt"));
        SetRoute(new TranslationRouteSelection("es", "fr", TranslationRoutingKind.Pivot, IsAvailable: true, "madlad400", "MADLAD-400 pivot", "fake-madlad400", "madlad400-mt", EngineFamily: "madlad"));
    }

    public Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
        string sourceLanguage,
        CancellationToken cancellationToken)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage)
            ?? throw new InvalidOperationException("Source language is required.");
        return Task.FromResult<IReadOnlyList<TranslationTargetLanguageOption>>(
            supportedTargetsBySourceLanguage.TryGetValue(normalizedSourceLanguage, out List<TranslationTargetLanguageOption>? options)
                ? options.ToArray()
                : []);
    }

    public Task<TranslationRouteSelection> ResolveRouteAsync(
        string sourceLanguage,
        string targetLanguage,
        CancellationToken cancellationToken,
        string? preferredModelAlias = null)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage)
            ?? throw new InvalidOperationException("Source language is required.");
        string normalizedTargetLanguage = NormalizeLanguageCode(targetLanguage)
            ?? throw new InvalidOperationException("Target language is required.");

        if (routes.TryGetValue((normalizedSourceLanguage, normalizedTargetLanguage), out TranslationRouteSelection? route))
        {
            return Task.FromResult(route);
        }

        return Task.FromResult(new TranslationRouteSelection(
            normalizedSourceLanguage,
            normalizedTargetLanguage,
            TranslationRoutingKind.Unavailable,
            IsAvailable: false,
            ProviderName: "none",
            RouteDetail: "Unavailable",
            UnavailableReason: $"No fake route is configured for {normalizedSourceLanguage} -> {normalizedTargetLanguage}."));
    }

    public void SetSupportedTargetLanguages(string sourceLanguage, params TranslationTargetLanguageOption[] options)
    {
        string normalizedSourceLanguage = NormalizeLanguageCode(sourceLanguage)
            ?? throw new InvalidOperationException("Source language is required.");
        supportedTargetsBySourceLanguage[normalizedSourceLanguage] = options.ToList();
    }

    public void SetRoute(TranslationRouteSelection route)
    {
        routes[(NormalizeLanguageCode(route.SourceLanguage)!, NormalizeLanguageCode(route.TargetLanguage)!)] = route;
    }

    private static string? NormalizeLanguageCode(string? languageCode) =>
        string.IsNullOrWhiteSpace(languageCode)
            ? null
            : languageCode.Trim().ToLowerInvariant();
}
