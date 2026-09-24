using System.Reflection;
using Trackdub.Contracts;
using Trackdub.Domain.StageRuns;

namespace Trackdub.Sdk.Tests;

public sealed class DubbingExportFormatTests
{
    [Fact]
    public void ResolveExportContainer_DefaultsToMp4()
    {
        Assert.Equal(
            ExportOutputContainer.Mp4,
            TrackdubDubbingEngine.ResolveExportContainer(null));
    }

    [Fact]
    public void ResolveExportContainer_MapsMkv()
    {
        Assert.Equal(
            ExportOutputContainer.Mkv,
            TrackdubDubbingEngine.ResolveExportContainer("mkv"));
    }

    [Fact]
    public void ResolveExportOutputPath_UsesContainerExtension()
    {
        string projectRoot = Path.Combine(Path.GetTempPath(), $"trackdub-{Guid.NewGuid():N}");

        string outputPath = TrackdubDubbingEngine.ResolveExportOutputPath(projectRoot, ExportOutputContainer.Mkv);

        Assert.Equal(Path.Combine(projectRoot, "exports", "dubbed.mkv"), outputPath);
    }

    [Theory]
    [InlineData(StageNames.Translation, TranslationModelOverrideSettings.DeepLModelAlias, true)]
    [InlineData(StageNames.Translation, TranslationModelOverrideSettings.GeminiTranslationCloudAlias, true)]
    [InlineData(StageNames.Translation, TranslationModelOverrideSettings.OpenAiGptCloudAlias, true)]
    [InlineData(StageNames.Translation, "madlad400", false)]
    [InlineData(StageNames.Asr, AsrModelOverrideSettings.GeminiAsrCloudAlias, true)]
    [InlineData(StageNames.Asr, AsrModelOverrideSettings.OpenAiWhisperCloudAlias, true)]
    [InlineData(StageNames.Asr, "qwen3-asr-0.6b", false)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.ElevenLabsCloudAlias, true)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.OpenAiTtsCloudAlias, true)]
    [InlineData(StageNames.Tts, TtsModelOverrideSettings.GoogleTtsCloudAlias, true)]
    [InlineData(StageNames.Tts, "kokoro-onnx", false)]
    [InlineData(StageNames.TextRefinementAsr, "gemini-refinement-cloud", true)]
    [InlineData(StageNames.TextRefinementAsr, "qwen-refinement", false)]
    public void ShouldSkipModelPreFlight_handles_cloud_and_local_models(
        string stageName,
        string modelAlias,
        bool expectedSkip)
    {
        bool skip = InvokeShouldSkipModelPreFlight(
            stageName,
            new Dictionary<string, string>
            {
                [stageName] = modelAlias
            });

        Assert.Equal(expectedSkip, skip);
    }

    private static bool InvokeShouldSkipModelPreFlight(
        string stageName,
        IReadOnlyDictionary<string, string>? modelPreferences)
    {
        MethodInfo method = typeof(TrackdubDubbingEngine).GetMethod(
            "ShouldSkipModelPreFlight",
            BindingFlags.NonPublic | BindingFlags.Static)
            ?? throw new MissingMethodException(nameof(TrackdubDubbingEngine), "ShouldSkipModelPreFlight");
        return (bool)method.Invoke(null, [stageName, modelPreferences])!;
    }
}
