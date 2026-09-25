using Trackdub.Contracts;
using Trackdub.Composition.Translation;
using Trackdub.Composition.Tts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Inference.Tests;

public sealed class GeminiCloudRoutingTests
{
    [Fact]
    public async Task ResolveRouteAsync_with_gemini_alias_requires_configured_api_key()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider(null));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias);

        Assert.False(route.IsAvailable);
        Assert.Equal("gemini", route.ProviderName);
        Assert.Equal(TranslationRoutingKind.Unavailable, route.RoutingKind);
        Assert.Contains("Gemini API key is not configured", route.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task ResolveRouteAsync_with_gemini_alias_returns_cloud_route_when_api_key_exists()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider("test-gemini-key"));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias);

        Assert.True(route.IsAvailable);
        Assert.Equal("gemini", route.ProviderName);
        Assert.Equal("Google Gemini Cloud API", route.RouteDetail);
        Assert.Equal(TranslationRoutingKind.Direct, route.RoutingKind);
        Assert.Equal(TranslationModelOverrideSettings.GeminiTranslationCloudAlias, route.PreferredModelAlias);
        Assert.Equal("gemini-translation-cloud", route.EngineFamily);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task ResolveRouteAsync_with_openai_alias_requires_configured_api_key()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider(null));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias);

        Assert.False(route.IsAvailable);
        Assert.Equal("openai", route.ProviderName);
        Assert.Equal(TranslationRoutingKind.Unavailable, route.RoutingKind);
        Assert.Contains("OpenAI API key is not configured", route.UnavailableReason, StringComparison.Ordinal);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task ResolveRouteAsync_with_openai_alias_returns_cloud_route_when_api_key_exists()
    {
        var localRouter = new ThrowingTranslationLanguageRouter();
        var router = new CloudAwareTranslationLanguageRouter(
            localRouter,
            new StaticCloudApiKeyProvider("test-openai-key"));

        TranslationRouteSelection route = await router.ResolveRouteAsync(
            "en",
            "es",
            CancellationToken.None,
            preferredModelAlias: TranslationModelOverrideSettings.OpenAiGptCloudAlias);

        Assert.True(route.IsAvailable);
        Assert.Equal("openai", route.ProviderName);
        Assert.Equal("OpenAI GPT Cloud API", route.RouteDetail);
        Assert.Equal(TranslationRoutingKind.Direct, route.RoutingKind);
        Assert.Equal(TranslationModelOverrideSettings.OpenAiGptCloudAlias, route.PreferredModelAlias);
        Assert.Equal("openai-gpt-cloud", route.EngineFamily);
        Assert.Equal(0, localRouter.ResolveCalls);
    }

    [Fact]
    public async Task TranslateAsync_with_gemini_alias_uses_gemini_engine_and_reports_metadata()
    {
        var localEngine = new StubTranslationEngine("local");
        var deeplEngine = new StubTranslationEngine("deepl");
        var openaiEngine = new StubTranslationEngine("openai");
        var geminiEngine = new StubTranslationEngine(
            "gemini-translated",
            new TranslationExecutionMetadata(
                "gemini",
                ModelId: "gemini-2.5-flash",
                ModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
                SelectedExecutionProvider: "cloud",
                TranslationRoutingKind.Direct),
            new StageRuntimeExecutionSummary(
                RequestedProvider: "cloud",
                SelectedProvider: "cloud",
                ModelId: "gemini-2.5-flash",
                ModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
                BootstrapDetail: "Google Gemini Cloud API"));

        var engine = new CloudAwareTranslationEngine(localEngine, deeplEngine, openaiEngine, geminiEngine);

        IReadOnlyList<TranslatedTextSegment> result = await engine.TranslateAsync(
            new TranslationRequest(
                "en",
                "es",
                [new TranslationInputSegment(0, 0, 1, "Hello world")],
                PreferredModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias),
            CancellationToken.None);

        Assert.Equal("gemini-translated", result[0].Text);
        Assert.Equal(0, localEngine.CallCount);
        Assert.Equal(0, deeplEngine.CallCount);
        Assert.Equal(0, openaiEngine.CallCount);
        Assert.Equal(1, geminiEngine.CallCount);
        Assert.Equal("gemini", engine.LastExecutionMetadata?.ProviderName);
        Assert.Equal("gemini-2.5-flash", engine.LastExecutionMetadata?.ModelId);
        Assert.Equal("cloud", engine.LastExecutionSummary?.SelectedProvider);
    }

    [Fact]
    public async Task SynthesizeAsync_with_gemini_tts_alias_routes_to_gemini_tts_engine()
    {
        var localEngine = new StubTtsEngine("local");
        var elevenLabsEngine = new StubTtsEngine("elevenlabs");
        var openAiEngine = new StubTtsEngine("openai");
        var googleEngine = new StubTtsEngine("google");
        var geminiEngine = new StubTtsEngine("gemini");

        var engine = new CloudAwareTtsEngine(
            localEngine,
            elevenLabsEngine,
            openAiEngine,
            googleEngine,
            geminiEngine);

        TtsSynthesisResult result = await engine.SynthesizeAsync(
            new TtsSynthesisRequest(
                "Hello world",
                "en",
                new VoiceCatalogEntry("Kore", "en", "neutral", "Kore"),
                Options: new InferenceRequestOptions(PreferredModelAlias: "gemini-tts-cloud")),
            CancellationToken.None);

        Assert.Equal("gemini", result.Provider);
        Assert.Equal(1, geminiEngine.CallCount);
        Assert.Equal(0, localEngine.CallCount);
        Assert.Equal(0, googleEngine.CallCount);
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

    private sealed class StubTtsEngine(string providerName) : ITtsEngine
    {
        public int CallCount { get; private set; }

        public Task<TtsSynthesisResult> SynthesizeAsync(
            TtsSynthesisRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return Task.FromResult(new TtsSynthesisResult(
                WavBytes: [1, 2, 3],
                DurationSamples: 100,
                SampleRate: 24000,
                ModelId: "test-model",
                VoiceId: request.Voice.VoiceId,
                Provider: providerName));
        }
    }
}
