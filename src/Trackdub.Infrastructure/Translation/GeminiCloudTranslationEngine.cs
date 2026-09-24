using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Infrastructure.Translation;

public sealed class GeminiCloudTranslationEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider)
    : ITranslationEngine, ITranslationExecutionMetadataReporter, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "gemini";
    public const string ProviderName = "gemini";
    public const string EngineFamilyName = "gemini-translation-cloud";

    public const string DefaultModel = "gemini-2.5-flash";
    public const string QualityModel = "gemini-2.5-pro";
    private const string EndpointBase = "https://generativelanguage.googleapis.com/v1beta/models";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

    public TranslationExecutionMetadata? LastExecutionMetadata { get; private set; }

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<IReadOnlyList<TranslatedTextSegment>> TranslateAsync(
        TranslationRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Segments.Count == 0)
        {
            return [];
        }

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);
        string model = ResolveModel(request);

        var promptBuilder = new StringBuilder();
        promptBuilder.Append(
            $"You are an expert dubbing translation engine. Translate the dialogue from {request.SourceLanguage} to {request.TargetLanguage} for voice dubbing. " +
            "Maintain natural conversational flow, emotional resonance, and approximate syllabic pacing where possible. " +
            "Return ONLY a valid JSON array of translated strings in the same order, with exactly the same number of elements. Do not include markdown formatting or commentary.");

        if (request.GlossaryHints is { Count: > 0 })
        {
            promptBuilder.Append(" Adhere strictly to the following glossary terms: ");
            promptBuilder.Append(string.Join(", ", request.GlossaryHints.Select(g => $"{g.SourceTerm} -> {g.TargetTerm}")));
            promptBuilder.Append('.');
        }

        string systemInstruction = promptBuilder.ToString();

        string userContent = JsonSerializer.Serialize(
            request.Segments.Select(s => s.Text).ToArray(),
            JsonOptions);

        GeminiRequest payload = new(
            SystemInstruction: new GeminiContent([new GeminiPart(systemInstruction)]),
            Contents: [new GeminiContent([new GeminiPart(userContent)])],
            GenerationConfig: new GeminiGenerationConfig("application/json"));

        string endpoint = $"{EndpointBase}/{model}:generateContent?key={Uri.EscapeDataString(apiKey)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        string responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"Gemini translation failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        GeminiResponse? geminiResponse = JsonSerializer.Deserialize<GeminiResponse>(responseBody, JsonOptions);
        string? content = geminiResponse?.Candidates?.FirstOrDefault()?.Content?.Parts?.FirstOrDefault()?.Text;
        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException("Gemini returned an empty translation response.");
        }

        string[]? translations = TryParseTranslationArray(content);
        if (translations is null || translations.Length != request.Segments.Count)
        {
            throw new InvalidOperationException(
                $"Gemini returned {translations?.Length ?? 0} translation result(s) for {request.Segments.Count} input segment(s).");
        }

        LastExecutionMetadata = new TranslationExecutionMetadata(
            ProviderName,
            ModelId: model,
            ModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
            SelectedExecutionProvider: "cloud",
            TranslationRoutingKind.Direct);
        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelId: model,
            ModelAlias: TranslationModelOverrideSettings.GeminiTranslationCloudAlias,
            BootstrapDetail: "Google Gemini Cloud API");

        var results = new List<TranslatedTextSegment>(request.Segments.Count);
        for (int i = 0; i < request.Segments.Count; i++)
        {
            TranslationInputSegment source = request.Segments[i];
            results.Add(new TranslatedTextSegment(
                source.Index,
                source.StartSeconds,
                source.EndSeconds,
                translations[i]));
        }

        return results;
    }

    private static string ResolveModel(TranslationRequest request)
    {
        string? envModel = Environment.GetEnvironmentVariable("TRACKDUB_GEMINI_TRANSLATION_MODEL");
        if (!string.IsNullOrWhiteSpace(envModel))
        {
            return envModel.Trim();
        }

        if (string.Equals(request.PreferredModelVariantAlias, "pro", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(request.PreferredModelAlias, QualityModel, StringComparison.OrdinalIgnoreCase) ||
            request.PreferredModelAlias?.Contains("pro", StringComparison.OrdinalIgnoreCase) == true)
        {
            return QualityModel;
        }

        return DefaultModel;
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "Gemini API key is not configured. Set GEMINI_API_KEY or TRACKDUB_GEMINI_API_KEY.");
        }

        return apiKey.Trim();
    }

    private static string StripMarkdownFences(string text)
    {
        string trimmed = text.Trim();
        if (trimmed.StartsWith("```json", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[7..];
        }
        else if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[3..];
        }

        if (trimmed.EndsWith("```", StringComparison.Ordinal))
        {
            trimmed = trimmed[..^3];
        }

        return trimmed.Trim();
    }

    private static string[]? TryParseTranslationArray(string content)
    {
        string stripped = StripMarkdownFences(content);
        try
        {
            using JsonDocument doc = JsonDocument.Parse(stripped);
            if (doc.RootElement.ValueKind == JsonValueKind.Array)
            {
                return doc.RootElement.EnumerateArray()
                    .Select(e => e.GetString() ?? string.Empty)
                    .ToArray();
            }
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private sealed record GeminiRequest(
        [property: JsonPropertyName("system_instruction")] GeminiContent SystemInstruction,
        [property: JsonPropertyName("contents")] GeminiContent[] Contents,
        [property: JsonPropertyName("generationConfig")] GeminiGenerationConfig GenerationConfig);

    private sealed record GeminiContent(
        [property: JsonPropertyName("parts")] GeminiPart[] Parts,
        [property: JsonPropertyName("role")] string? Role = null);

    private sealed record GeminiPart(
        [property: JsonPropertyName("text")] string Text);

    private sealed record GeminiGenerationConfig(
        [property: JsonPropertyName("responseMimeType")] string ResponseMimeType);

    private sealed record GeminiResponse(
        [property: JsonPropertyName("candidates")] GeminiCandidate[]? Candidates);

    private sealed record GeminiCandidate(
        [property: JsonPropertyName("content")] GeminiContent? Content);
}
