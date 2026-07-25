using Trackdub.Contracts;
using Trackdub.Composition.Translation;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Tests;

public sealed class DeepLCloudRoutingTests
{
    [Fact]
    public async Task ResolveRouteAsync_with_deepl_alias_requires_configured_api_key()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider(null));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias);

        Assert.False(route.IsAvailable);
        Assert.Equal("deepl", route.ProviderName);
        Assert.Equal(TranslationRoutingKind.Unavailable, route.RoutingKind);
        Assert.Contains("DeepL API key is not configured", route.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task ResolveRouteAsync_with_deepl_alias_returns_cloud_route_when_api_key_exists()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider("test-key:fx"));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias);

        Assert.True(route.IsAvailable);
        Assert.Equal("deepl", route.ProviderName);
        Assert.Equal("DeepL Cloud API", route.RouteDetail);
        Assert.Equal(TranslationRoutingKind.Direct, route.RoutingKind);
        Assert.Equal(TranslationModelOverrideSettings.DeepLModelAlias, route.PreferredModelAlias);
        Assert.Equal("deepl-cloud", route.EngineFamily);
        Assert.Null(route.ModelId);
        Assert.Null(route.ResolvedModelEntryPath);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task ResolveRouteAsync_without_deepl_alias_uses_local_router()
    {
        var localRoute = new TranslationRouteSelection(
            "en",
            "es",
            TranslationRoutingKind.Direct,
            IsAvailable: true,
            ProviderName: "opus-mt",
            RouteDetail: "Local");
        var localRouter = new StubTranslationLanguageRouter(localRoute);
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider("test-key:fx"));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None);

        Assert.Same(localRoute, route);
        Assert.Equal(1, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task ResolveRouteAsync_with_deepl_alias_returns_unavailable_for_unsupported_language()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider("test-key:fx"));

        // "xx" is not in DeepL's supported target language catalog.
        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "xx",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias);

        Assert.False(route.IsAvailable);
        Assert.Equal(TranslationRoutingKind.Unavailable, route.RoutingKind);
        Assert.Contains("not in Trackdub's DeepL language catalog", route.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task TranslateAsync_with_deepl_alias_uses_cloud_engine_and_reports_cloud_metadata()
    {
        var localEngine = new StubTranslationEngine("local");
        var cloudEngine = new StubTranslationEngine(
            "cloud",
            new TranslationExecutionMetadata(
                "deepl",
                ModelId: null,
                ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias,
                SelectedExecutionProvider: "cloud",
                TranslationRoutingKind.Direct),
            new StageRuntimeExecutionSummary(
                RequestedProvider: "cloud",
                SelectedProvider: "cloud",
                ModelAlias: TranslationModelOverrideSettings.DeepLModelAlias));
        var engine = new CloudAwareTranslationEngine(localEngine, cloudEngine, localEngine, localEngine);

        IReadOnlyList<TranslatedTextSegment> result = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0, 1, "Hello")],
                PreferredModelAlias: TranslationModelOverrideSettings.DeepLModelAlias),
            CancellationToken.None);

        Assert.Equal("cloud", result[0].Text);
        Assert.Equal(0, localEngine.CallCount);
        Assert.Equal(1, cloudEngine.CallCount);
        Assert.Equal("deepl", engine.LastExecutionMetadata?.ProviderName);
        Assert.Equal("cloud", engine.LastExecutionSummary?.SelectedProvider);
    }

    private sealed class StaticCloudApiKeyProvider(string? apiKey) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class ThrowingTranslationLanguageRouter : ITranslationLanguageRouter
    {
        public int ResolveCalls { get; private set; }

        public Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
            string sourceLanguage,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException("Local router should not be called.");

        public Task<TranslationRouteSelection> ResolveRouteAsync(
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken,
            string? preferredModelAlias = null)
        {
            ResolveCalls++;
            throw new InvalidOperationException("Local router should not be called.");
        }
    }

    private sealed class StubTranslationLanguageRouter(TranslationRouteSelection route) : ITranslationLanguageRouter
    {
        public int ResolveCalls { get; private set; }

        public Task<IReadOnlyList<TranslationTargetLanguageOption>> GetSupportedTargetLanguagesAsync(
            string sourceLanguage,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<TranslationTargetLanguageOption>>([]);

        public Task<TranslationRouteSelection> ResolveRouteAsync(
            string sourceLanguage,
            string targetLanguage,
            CancellationToken cancellationToken,
            string? preferredModelAlias = null)
        {
            ResolveCalls++;
            return Task.FromResult(route);
        }
    }

    private sealed class StubTranslationEngine(
        string translatedText,
        TranslationExecutionMetadata? metadata = null,
        StageRuntimeExecutionSummary? summary = null)
        : ITranslationEngine, ITranslationExecutionMetadataReporter, IStageRuntimeExecutionReporter
    {
        public int CallCount { get; private set; }

        public TranslationExecutionMetadata? LastExecutionMetadata { get; private set; } = metadata;

        public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; } = summary;

        public Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
            TranslationRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            TranslationInputSegment segment = request.Segments[0];
            return Task.FromResult<IReadOnlyList<TranslatedTextSegment>>(
            [
                new TranslatedTextSegment(
                    segment.Index,
                    segment.StartSeconds,
                    segment.EndSeconds,
                    translatedText)
            ]);
        }
    }
}
