using System.Text.Json;
using Trackdub.SidecarHost;
using Xunit;

namespace Trackdub.SidecarHost.Tests;

public sealed class ProtocolRoundtripTests
{
    [Fact]
    public void SidecarRequest_Load_SerializesWithProtocolWireNames()
    {
        var plan = new SidecarLoadPlan("/models/chatterbox", ["CUDA", "CPU"], RequirePreferred: false, VoicePromptPath: "/voices/ref.wav");
        var request = new SidecarRequest("req-1", "load", Plan: plan);

        string json = JsonSerializer.Serialize(request, ProtocolJson.Options);
        Assert.Contains("\"op\":\"load\"", json);
        Assert.Contains("\"voicePromptPath\"", json);
        Assert.Contains("\"requirePreferred\":false", json);
        Assert.Contains("\"providers\":[\"CUDA\",\"CPU\"]", json);
    }

    [Fact]
    public void SidecarResponse_Ok_ParsesWithProtocolWireNames()
    {
        const string wire = "{\"id\":\"req-3\",\"status\":\"ok\",\"sampleRate\":24000,\"protocolVersion\":1," +
            "\"outputs\":{\"audio\":{\"dtype\":\"int16\",\"shape\":[480],\"data\":\"AAAA\"}}}";
        var resp = JsonSerializer.Deserialize<SidecarResponse>(wire, ProtocolJson.Options);

        Assert.NotNull(resp);
        Assert.Equal("ok", resp.Status);
        Assert.Equal(24000, resp.SampleRate);
        Assert.Equal(1, resp.ProtocolVersion);
        Assert.NotNull(resp.Outputs);
        Assert.True(resp.Outputs.ContainsKey("audio"));
        Assert.Equal("int16", resp.Outputs["audio"].Dtype);
        Assert.Equal([480L], resp.Outputs["audio"].Shape);
    }

    [Fact]
    public void SidecarTensor_Utf8Envelope_MatchesProtocolShape()
    {
        var tensor = SidecarTensor.Utf8("hola");
        string json = JsonSerializer.Serialize(tensor, ProtocolJson.Options);
        Assert.Contains("\"dtype\":\"utf8\"", json);
        Assert.Contains("\"shape\":[4]", json);   // 4 UTF-8 bytes
    }

    [Fact]
    public void SidecarTensor_Int16_RoundtripsBase64Data()
    {
        var tensor = new SidecarTensor("int16", [2], [0x01, 0x02, 0x03, 0x04]);
        string json = JsonSerializer.Serialize(tensor, ProtocolJson.Options);
        var back = JsonSerializer.Deserialize<SidecarTensor>(json, ProtocolJson.Options);
        Assert.NotNull(back);
        Assert.Equal(tensor.Data, back.Data);
        Assert.Equal([2L], back.Shape);
    }

    [Fact]
    public void SidecarResponse_ErrorResponse_ParsesReasonVocabulary()
    {
        const string wire = "{\"id\":null,\"status\":\"error\",\"reason\":\"invalid-json\",\"detail\":\"boom\",\"protocolVersion\":1}";
        var resp = JsonSerializer.Deserialize<SidecarResponse>(wire, ProtocolJson.Options);

        Assert.NotNull(resp);
        Assert.Equal("error", resp.Status);
        Assert.Equal("invalid-json", resp.Reason);
        Assert.Null(resp.Id);
    }
}
