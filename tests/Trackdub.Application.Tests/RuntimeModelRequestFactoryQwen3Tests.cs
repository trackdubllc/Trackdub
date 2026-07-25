using Trackdub.Application.Transcripts;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Application.Tests;

public sealed class RuntimeModelRequestFactoryQwen3Tests
{
    [Fact]
    public void CreateTtsRequest_Qwen3Override_UsesCustomVoiceWhenNotCloning()
    {
        var options = new RuntimeModelRequestOptions(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TtsModelOverride: TtsModelOverride.Qwen3Tts);

        RuntimeModelRequest request = RuntimeModelRequestFactory.CreateTtsRequest(
            options,
            requiresVoiceClone: false,
            preferredTier: "quality");

        Assert.Equal(Qwen3TtsDefaults.CustomVoice17Alias, request.PreferredModelAlias);
        Assert.True(request.RequirePreferredModelAlias);
    }

    [Fact]
    public void CreateTtsRequest_Qwen3Override_UsesBaseWhenCloning()
    {
        var options = new RuntimeModelRequestOptions(
            AsrModelOverride.Auto,
            IsDevBuild: false,
            HardwareOverrides: new Dictionary<string, ExecutionProviderKind>(),
            TtsModelOverride: TtsModelOverride.Qwen3Tts);

        RuntimeModelRequest request = RuntimeModelRequestFactory.CreateTtsRequest(
            options,
            requiresVoiceClone: true,
            preferredTier: "balanced");

        Assert.Equal(Qwen3TtsDefaults.Base06Alias, request.PreferredModelAlias);
    }
}
