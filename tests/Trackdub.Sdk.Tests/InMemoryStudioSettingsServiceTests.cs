using Trackdub.Composition.Headless;
using Trackdub.Contracts;
using Trackdub.Domain;

namespace Trackdub.Sdk.Tests;

public sealed class InMemoryStudioSettingsServiceTests
{
    [Fact]
    public async Task LoadAsync_returns_configured_hardware_overrides()
    {
        var service = new InMemoryStudioSettingsService(new HeadlessTrackdubOptions
        {
            HardwareOverrides = new Dictionary<string, ExecutionProviderKind>
            {
                ["Vad"] = ExecutionProviderKind.DirectMl,
                ["AsrGenAi"] = ExecutionProviderKind.DirectMl,
                ["AsrOnnxRuntime"] = ExecutionProviderKind.DirectMl,
                ["AsrNemotron"] = ExecutionProviderKind.DirectMl,
                ["Separation"] = ExecutionProviderKind.DirectMl,
                ["Diarization"] = ExecutionProviderKind.DirectMl,
                ["Translation"] = ExecutionProviderKind.DirectMl,
                ["Tts"] = ExecutionProviderKind.DirectMl,
                ["LipSync"] = ExecutionProviderKind.DirectMl,
                ["LipSynthesis"] = ExecutionProviderKind.DirectMl,
            },
        });

        StudioSettings settings = await service.LoadAsync(CancellationToken.None);

        Assert.NotNull(settings.HardwareOverrides);
        Assert.Equal(ExecutionProviderKind.DirectMl, settings.HardwareOverrides!["AsrNemotron"]);
        Assert.Equal(ExecutionProviderKind.DirectMl, settings.HardwareOverrides["LipSync"]);
        Assert.Equal(ExecutionProviderKind.DirectMl, settings.HardwareOverrides["LipSynthesis"]);
    }
}
