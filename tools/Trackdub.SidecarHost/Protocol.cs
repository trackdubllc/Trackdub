using System.Text.Json;
using System.Text.Json.Serialization;

namespace Trackdub.SidecarHost;

/// <summary>
/// Typed tensor envelope per workers/PROTOCOL.md: {dtype, shape, data}.
/// </summary>
public sealed record SidecarTensor(
    [property: JsonPropertyName("dtype")] string Dtype,
    [property: JsonPropertyName("shape")] long[] Shape,
    [property: JsonPropertyName("data")] byte[] Data)
{
    public static SidecarTensor Utf8(string text)
    {
        byte[] bytes = System.Text.Encoding.UTF8.GetBytes(text);
        return new SidecarTensor("utf8", [bytes.Length], bytes);
    }

    public static SidecarTensor Int16(byte[] pcm, long sampleCount) =>
        new("int16", [sampleCount], pcm);

    public string ToJson() => JsonSerializer.Serialize(this, ProtocolJson.Options);
}

public sealed record SidecarLoadPlan(
    [property: JsonPropertyName("model")] string Model,
    [property: JsonPropertyName("providers")] string[] Providers,
    [property: JsonPropertyName("requirePreferred")] bool RequirePreferred,
    [property: JsonPropertyName("voicePromptPath")] string? VoicePromptPath = null);

public sealed record SidecarRequest(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("op")] string Op,
    [property: JsonPropertyName("plan")] SidecarLoadPlan? Plan = null,
    [property: JsonPropertyName("inputs")] Dictionary<string, SidecarTensor>? Inputs = null);

public sealed record SidecarResponse(
    [property: JsonPropertyName("id")] string? Id,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("reason")] string? Reason = null,
    [property: JsonPropertyName("detail")] string? Detail = null,
    [property: JsonPropertyName("modelLoaded")] bool? ModelLoaded = null,
    [property: JsonPropertyName("activeProvider")] string? ActiveProvider = null,
    [property: JsonPropertyName("model")] string? Model = null,
    [property: JsonPropertyName("sampleRate")] int? SampleRate = null,
    [property: JsonPropertyName("voicePrompt")] string? VoicePrompt = null,
    [property: JsonPropertyName("outputs")] Dictionary<string, SidecarTensor>? Outputs = null,
    [property: JsonPropertyName("protocolVersion")] int ProtocolVersion = 0);

public static class ProtocolJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonSerializerOptions.Default.PropertyNamingPolicy,
    };
}
