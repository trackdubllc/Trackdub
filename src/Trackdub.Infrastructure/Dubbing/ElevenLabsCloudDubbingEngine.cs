using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;

namespace Trackdub.Infrastructure.Dubbing;

public sealed class ElevenLabsCloudDubbingEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider)
    : ICloudDubbingEngine
{
    public const string ProviderKey = "elevenlabs";

    private const string ApiBase = "https://api.elevenlabs.io/v1";
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(30);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

    public async Task<CloudDubbingResult> DubAsync(
        CloudDubbingRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        // Step 1 — Submit dubbing job
        (string dubbingId, double expectedDurationSec) = await SubmitDubbingJobAsync(
            request, apiKey, cancellationToken).ConfigureAwait(false);

        // Step 2 — Poll until complete
        await PollUntilDubbedAsync(dubbingId, apiKey, cancellationToken).ConfigureAwait(false);

        // Step 3 — Download dubbed audio
        byte[] audioBytes = await DownloadDubbedAudioAsync(
            dubbingId, request.TargetLanguage, apiKey, cancellationToken).ConfigureAwait(false);

        return new CloudDubbingResult(
            AudioBytes: audioBytes,
            TargetLanguage: request.TargetLanguage,
            EstimatedDuration: TimeSpan.FromSeconds(expectedDurationSec));
    }

    private async Task<(string DubbingId, double ExpectedDurationSec)> SubmitDubbingJobAsync(
        CloudDubbingRequest request,
        string apiKey,
        CancellationToken cancellationToken)
    {
        byte[] fileBytes = await File.ReadAllBytesAsync(
            request.MediaFilePath, cancellationToken).ConfigureAwait(false);

        string fileName = Path.GetFileName(request.MediaFilePath);
        string extension = Path.GetExtension(request.MediaFilePath).TrimStart('.').ToLowerInvariant();
        string mimeType = extension switch
        {
            "mp4" => "video/mp4",
            "mkv" => "video/x-matroska",
            "mp3" => "audio/mpeg",
            "wav" => "audio/wav",
            _ => "application/octet-stream"
        };

        using var form = new MultipartFormDataContent();
        form.Add(
            new ByteArrayContent(fileBytes) { Headers = { ContentType = new(mimeType) } },
            "file",
            fileName);
        form.Add(new StringContent(request.SourceLanguage), "source_lang");
        form.Add(new StringContent(request.TargetLanguage), "target_lang");
        form.Add(new StringContent("automatic"), "mode");

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, $"{ApiBase}/dubbing");
        httpRequest.Headers.Add("xi-api-key", apiKey);
        httpRequest.Content = form;

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        string responseBody = response.Content is null
            ? string.Empty
            : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                $"ElevenLabs dubbing submission failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {responseBody}");
        }

        ElevenLabsDubbingSubmitResponse? submitResponse =
            JsonSerializer.Deserialize<ElevenLabsDubbingSubmitResponse>(responseBody, JsonOptions);

        if (string.IsNullOrWhiteSpace(submitResponse?.DubbingId))
        {
            throw new InvalidOperationException(
                $"ElevenLabs did not return a dubbing_id. Response: {responseBody}");
        }

        return (submitResponse.DubbingId, submitResponse.ExpectedDurationSec ?? 0d);
    }

    private async Task PollUntilDubbedAsync(
        string dubbingId,
        string apiKey,
        CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = DateTimeOffset.UtcNow + MaxWait;
        using var timer = new PeriodicTimer(PollInterval);

        while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
        {
            if (DateTimeOffset.UtcNow > deadline)
            {
                throw new TimeoutException(
                    $"ElevenLabs dubbing job '{dubbingId}' did not complete within {MaxWait.TotalMinutes} minutes.");
            }

            using var statusRequest = new HttpRequestMessage(HttpMethod.Get, $"{ApiBase}/dubbing/{dubbingId}");
            statusRequest.Headers.Add("xi-api-key", apiKey);

            using HttpResponseMessage statusResponse = await httpClient
                .SendAsync(statusRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);

            string statusBody = statusResponse.Content is null
                ? string.Empty
                : await statusResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!statusResponse.IsSuccessStatusCode)
            {
                throw new InvalidOperationException(
                    $"ElevenLabs dubbing status check failed with HTTP {(int)statusResponse.StatusCode}: {statusBody}");
            }

            ElevenLabsDubbingStatusResponse? statusResult =
                JsonSerializer.Deserialize<ElevenLabsDubbingStatusResponse>(statusBody, JsonOptions);

            switch (statusResult?.Status?.ToLowerInvariant())
            {
                case "dubbed":
                    return;
                case "failed":
                    throw new InvalidOperationException(
                        $"ElevenLabs dubbing job '{dubbingId}' failed. Response: {statusBody}");
                    // "pending" or "in_progress"
                    // — keep polling
            }
        }
    }

    private async Task<byte[]> DownloadDubbedAudioAsync(
        string dubbingId,
        string languageCode,
        string apiKey,
        CancellationToken cancellationToken)
    {
        string downloadUrl = $"{ApiBase}/dubbing/{dubbingId}/audio/{Uri.EscapeDataString(languageCode)}";

        using var httpRequest = new HttpRequestMessage(HttpMethod.Get, downloadUrl);
        httpRequest.Headers.Add("xi-api-key", apiKey);

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ElevenLabs dubbed audio download failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorBody}");
        }

        return await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<string> ResolveApiKeyAsync(CancellationToken cancellationToken)
    {
        string? apiKey = await apiKeyProvider.GetApiKeyAsync(ProviderKey, cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            throw new InvalidOperationException(
                "ElevenLabs API key is not configured. Set ELEVENLABS_API_KEY or TRACKDUB_ELEVENLABS_API_KEY.");
        }

        return apiKey.Trim();
    }

    private sealed record ElevenLabsDubbingSubmitResponse(
        [property: JsonPropertyName("dubbing_id")] string? DubbingId,
        [property: JsonPropertyName("expected_duration_sec")] double? ExpectedDurationSec);

    private sealed record ElevenLabsDubbingStatusResponse(
        [property: JsonPropertyName("dubbing_id")] string? DubbingId,
        [property: JsonPropertyName("status")] string? Status);
}
