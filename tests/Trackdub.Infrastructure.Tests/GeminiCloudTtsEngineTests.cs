using System.Net;
using System.Text;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Tts;

namespace Trackdub.Infrastructure.Tests;

public sealed class GeminiCloudTtsEngineTests
{
    [Fact]
    public async Task SynthesizeAsync_sends_tts_request_and_parses_audio_response()
    {
        byte[] dummyPcm = [0, 0, 10, 0, 20, 0, 30, 0];
        string base64Data = Convert.ToBase64String(dummyPcm);

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent($$"""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "inlineData": {
                              "mimeType": "audio/pcm;rate=24000",
                              "data": "{{base64Data}}"
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
                """)
        });

        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTtsEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"));

        TtsSynthesisResult result = await engine.SynthesizeAsync(
            new TtsSynthesisRequest(
                "Hello world",
                "en",
                new VoiceCatalogEntry("Kore", "en", "neutral", "Kore")),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-3.8-flash-tts:generateContent", handler.RequestUri.ToString());
        Assert.Contains("key=test-gemini-key", handler.RequestUri.Query);
        Assert.Equal("gemini", result.Provider);
        Assert.Equal("gemini-3.8-flash-tts", result.ModelId);
        Assert.Equal("Kore", result.VoiceId);
        Assert.Equal(24000, result.SampleRate);
        Assert.Equal(4, result.DurationSamples); // 8 bytes PCM / 2 = 4 samples
        Assert.Equal(44 + 8, result.WavBytes.Length); // 44-byte WAV header + 8 bytes
        Assert.Equal("gemini-3.8-flash-tts", engine.LastExecutionSummary?.ModelId);
    }

    [Fact]
    public async Task SynthesizeAsync_selects_lite_tts_model_when_variant_is_lite()
    {
        byte[] dummyPcm = [0, 0, 1, 0];
        string base64Data = Convert.ToBase64String(dummyPcm);

        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent($$"""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "inlineData": {
                              "mimeType": "audio/pcm",
                              "data": "{{base64Data}}"
                            }
                          }
                        ]
                      }
                    }
                  ]
                }
                """)
        });

        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTtsEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"));

        TtsSynthesisResult result = await engine.SynthesizeAsync(
            new TtsSynthesisRequest(
                "Quick test",
                "en",
                new VoiceCatalogEntry("Kore", "en", "neutral", "Kore"),
                Options: new InferenceRequestOptions(PreferredModelVariantAlias: "gemini-3.8-flash-lite-tts")),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-3.8-flash-lite-tts:generateContent", handler.RequestUri.ToString());
        Assert.Equal("gemini-3.8-flash-lite-tts", result.ModelId);
    }

    [Fact]
    public async Task SynthesizeAsync_throws_when_api_key_is_missing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var engine = new GeminiCloudTtsEngine(
            httpClient,
            new StaticCloudApiKeyProvider(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.SynthesizeAsync(
                new TtsSynthesisRequest(
                    "Test",
                    "en",
                    new VoiceCatalogEntry("Kore", "en", "neutral", "Kore")),
                TestContext.Current.CancellationToken));
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class StaticCloudApiKeyProvider(string? apiKey) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> handler) : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            return Task.FromResult(handler(request));
        }
    }
}
