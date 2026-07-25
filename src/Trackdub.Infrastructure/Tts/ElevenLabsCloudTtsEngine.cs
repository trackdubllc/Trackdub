using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Trackdub.Contracts;
using Trackdub.Contracts.Pipeline;

namespace Trackdub.Infrastructure.Tts;

public sealed class ElevenLabsCloudTtsEngine(
    HttpClient httpClient,
    ICloudApiKeyProvider apiKeyProvider)
    : ITtsEngine, IStageRuntimeExecutionReporter
{
    public const string ProviderKey = "elevenlabs";
    public const string ProviderName = "elevenlabs";
    public const string EngineFamilyName = "elevenlabs-tts-cloud";

    private const string ApiBase = "https://api.elevenlabs.io/v1";
    private const string DefaultModel = "eleven_multilingual_v2";
    private const int OutputSampleRate = 24000;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly ICloudApiKeyProvider apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));

    public StageRuntimeExecutionSummary? LastExecutionSummary { get; private set; }

    public async Task<TtsSynthesisResult> SynthesizeAsync(
        TtsSynthesisRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        string apiKey = await ResolveApiKeyAsync(cancellationToken).ConfigureAwait(false);

        string voiceId = request.Voice.VoiceId;
        // output_format=pcm_24000 returns raw 16-bit signed LE PCM at 24 kHz
        string endpoint = $"{ApiBase}/text-to-speech/{Uri.EscapeDataString(voiceId)}?output_format=pcm_24000";

        ElevenLabsRequest payload = new(
            Text: request.Text,
            ModelId: DefaultModel,
            VoiceSettings: new ElevenLabsVoiceSettings(Stability: 0.5f, SimilarityBoost: 0.75f));

        using var httpRequest = new HttpRequestMessage(HttpMethod.Post, endpoint);
        httpRequest.Headers.Add("xi-api-key", apiKey);
        httpRequest.Content = new StringContent(
            JsonSerializer.Serialize(payload, JsonOptions),
            Encoding.UTF8,
            "application/json");

        using HttpResponseMessage response = await httpClient
            .SendAsync(httpRequest, HttpCompletionOption.ResponseContentRead, cancellationToken)
            .ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string errorBody = response.Content is null
                ? string.Empty
                : await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException(
                $"ElevenLabs TTS failed with HTTP {(int)response.StatusCode}: {response.ReasonPhrase}. {errorBody}");
        }

        byte[] pcmBytes = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
        byte[] wavBytes = WrapPcmInWav(pcmBytes, OutputSampleRate, channels: 1, bitsPerSample: 16);

        int durationSamples = pcmBytes.Length / 2; // 16-bit = 2 bytes per sample

        LastExecutionSummary = new StageRuntimeExecutionSummary(
            RequestedProvider: "cloud",
            SelectedProvider: "cloud",
            ModelAlias: TtsModelOverrideSettings.ElevenLabsCloudAlias,
            BootstrapDetail: "ElevenLabs Cloud API");

        return new TtsSynthesisResult(
            WavBytes: wavBytes,
            DurationSamples: durationSamples,
            SampleRate: OutputSampleRate,
            ModelId: DefaultModel,
            VoiceId: voiceId,
            Provider: ProviderName);
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

    private static byte[] WrapPcmInWav(byte[] pcmBytes, int sampleRate, short channels, short bitsPerSample)
    {
        int dataSize = pcmBytes.Length;
        byte[] wav = new byte[44 + dataSize];

        // RIFF header
        Encoding.ASCII.GetBytes("RIFF").CopyTo(wav, 0);
        BitConverter.GetBytes(36 + dataSize).CopyTo(wav, 4);
        Encoding.ASCII.GetBytes("WAVE").CopyTo(wav, 8);

        // fmt chunk
        Encoding.ASCII.GetBytes("fmt ").CopyTo(wav, 12);
        BitConverter.GetBytes(16).CopyTo(wav, 16);                                     // chunk size
        BitConverter.GetBytes((short)1).CopyTo(wav, 20);                               // PCM format
        BitConverter.GetBytes(channels).CopyTo(wav, 22);
        BitConverter.GetBytes(sampleRate).CopyTo(wav, 24);
        BitConverter.GetBytes(sampleRate * channels * bitsPerSample / 8).CopyTo(wav, 28); // byte rate
        BitConverter.GetBytes((short)(channels * bitsPerSample / 8)).CopyTo(wav, 32);  // block align
        BitConverter.GetBytes(bitsPerSample).CopyTo(wav, 34);

        // data chunk
        Encoding.ASCII.GetBytes("data").CopyTo(wav, 36);
        BitConverter.GetBytes(dataSize).CopyTo(wav, 40);
        pcmBytes.CopyTo(wav, 44);

        return wav;
    }

    private sealed record ElevenLabsRequest(
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("model_id")] string ModelId,
        [property: JsonPropertyName("voice_settings")] ElevenLabsVoiceSettings VoiceSettings);

    private sealed record ElevenLabsVoiceSettings(
        [property: JsonPropertyName("stability")] float Stability,
        [property: JsonPropertyName("similarity_boost")] float SimilarityBoost);
}
