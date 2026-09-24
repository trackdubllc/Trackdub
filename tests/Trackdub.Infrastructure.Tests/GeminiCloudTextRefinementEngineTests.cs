using System.Net;
using System.Text;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;
using Trackdub.Infrastructure.Transcripts;

namespace Trackdub.Infrastructure.Tests;

public sealed class GeminiCloudTextRefinementEngineTests
{
    [Fact]
    public async Task RefineAsync_sends_prompt_and_parses_refined_segments()
    {
        var handler = new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent("""
                {
                  "candidates": [
                    {
                      "content": {
                        "parts": [
                          {
                            "text": "```json\n[{\"index\": 0, \"start\": 0.0, \"end\": 1.5, \"text\": \"Hello world.\"}]\n```"
                          }
                        ]
                      }
                    }
                  ]
                }
                """)
        });
        using var httpClient = new HttpClient(handler);
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider("test-gemini-key"));

        IReadOnlyList<RefinedTextSegment> result = await engine.RefineAsync(
            new TextRefinementRequest(
                [new TextRefinementInputSegment(0, 0.0, 1.5, "hello world")],
                SourceLanguage: "en"),
            TestContext.Current.CancellationToken);

        Assert.NotNull(handler.RequestUri);
        Assert.Contains("models/gemini-2.5-flash:generateContent", handler.RequestUri.ToString());
        Assert.Single(result);
        Assert.Equal("Hello world.", result[0].RefinedText);
        Assert.Equal("gemini-refinement-cloud", engine.EngineFamily);
        Assert.Equal("cloud", engine.LastExecutionSummary?.SelectedProvider);
        Assert.Equal("gemini-2.5-flash", engine.LastExecutionSummary?.ModelId);
    }

    [Fact]
    public async Task RefineAsync_throws_when_api_key_is_missing()
    {
        using var httpClient = new HttpClient(new CapturingHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)));
        var engine = new GeminiCloudTextRefinementEngine(
            httpClient,
            new StaticCloudApiKeyProvider(null));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            engine.RefineAsync(
                new TextRefinementRequest(
                    [new TextRefinementInputSegment(0, 0.0, 1.5, "hello world")],
                    SourceLanguage: "en"),
                TestContext.Current.CancellationToken));
    }

    private static StringContent JsonContent(string json) =>
        new(json, Encoding.UTF8, "application/json");

    private sealed class StaticCloudApiKeyProvider(string? apiKey) : ICloudApiKeyProvider
    {
        public Task<string?> GetApiKeyAsync(string providerKey, CancellationToken cancellationToken) =>
            Task.FromResult(apiKey);
    }

    private sealed class CapturingHandler(Func<HttpRequestMessage, HttpResponseMessage> responseFactory)
        : HttpMessageHandler
    {
        public Uri? RequestUri { get; private set; }
        public HttpMethod? Method { get; private set; }
        public string RequestBody { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestUri = request.RequestUri;
            Method = request.Method;
            RequestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            return responseFactory(request);
        }
    }
}
